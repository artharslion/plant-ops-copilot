using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PlantOps.AI;
using PlantOps.Application.AI;
using PlantOps.Application.Alarms;
using PlantOps.Infrastructure.Persistence;
using Xunit;

namespace PlantOps.Tests;

[Collection("Database")]
public sealed class InvestigationAdviceServiceTests
{
    [DatabaseScenarioTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HistoricalRecoveryAndCurrentExceedanceStaySeparateThroughDraftConfirmation(bool qualifiesForRecovery)
    {
        var gateway = new MeasurementReadGateway();
        var clock = new AlarmTests.Clock();
        await using var session = new AssetManagementTests.Session(clock, await TestDatabase.IsolatedConnection(), configureServices: services =>
            services.AddSingleton<ILlmGateway>(gateway));
        await session.Initialize();
        await session.Login();
        var (fixture, alarm, recoveryAt, highAt) = await RecoveryAlarmFixture.Create(session, clock, qualifiesForRecovery);
        await using var scope = session.Factory.Services.CreateAsyncScope();
        var alarmPath = $"/api/v1/alarms/{alarm.Id}";
        var selected = (await session.Client.GetFromJsonAsync<AlarmDto>(alarmPath))!;
        Assert.Equal(recoveryAt, selected.RecoveredAt);
        Assert.Equal(qualifiesForRecovery ? "Recovered" : "Active", selected.State);
        Assert.Null(selected.AcknowledgedAt);

        using var started = await session.Client.PostAsync(alarmPath + "/investigations", null);
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var run = (await started.Content.ReadFromJsonAsync<InvestigationRunDto>())!;
        Assert.True(await scope.ServiceProvider.GetRequiredService<IInvestigationService>().ProcessNextAsync());
        var runPath = $"/api/v1/investigations/{run.Id}";
        run = (await session.Client.GetFromJsonAsync<InvestigationRunDto>(runPath))!;
        Assert.True(run.Status == "Succeeded", run.Status + ":" + run.FailureCode);

        var snapshot = Assert.Single(run.Evidence, x => x.Kind == "Alarm");
        using var savedRule = JsonDocument.Parse(selected.ThresholdSnapshot);
        Assert.Equal(6, savedRule.RootElement.GetProperty("ClearBelow").GetDouble());
        Assert.Equal(3000, savedRule.RootElement.GetProperty("ClearForMs").GetInt32());
        Assert.Contains(snapshot.Facts, fact => fact.Contains(selected.ThresholdSnapshot, StringComparison.Ordinal));
        Assert.Equal(snapshot.Facts, gateway.ProjectedFacts(snapshot.Id));
        var current = Assert.Single(run.Assessment!.Points);
        Assert.Equal("Trusted", current.CurrentTrust);
        Assert.Equal("Present", current.CurrentAnomaly);
        Assert.Equal(14, current.Observation!.Value);
        Assert.Equal(highAt, current.Observation.EffectiveAt);
        var currentEvidence = run.Evidence.Last(x => x.PointStatus?.PointId == fixture.Point.Id);
        var projected = gateway.ProjectedEvidence(currentEvidence.Id).GetProperty("point");
        Assert.Equal("Present", projected.GetProperty("currentAnomaly").GetString());
        Assert.Equal(highAt, projected.GetProperty("observation").GetProperty("effectiveAt").GetDateTimeOffset());

        var advice = InvestigationAdviceContract.ParseAndValidate(run.Result!.Value.GetRawText(), run.Evidence);
        Assert.Contains(advice.Findings, x => x.Scope == "Current" && x.Status == "AnomalySupported"
            && x.EvidenceRefs.Contains(currentEvidence.Id));
        if (recoveryAt is { } recordedAt)
        {
            var timestamp = recordedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
            Assert.Contains(snapshot.Facts, fact => fact.Contains(timestamp, StringComparison.Ordinal));
            var recovery = Assert.Single(run.Evidence, x => x.Kind == "RecoveryReadings");
            Assert.Contains(timestamp, recovery.Facts[0]);
            Assert.Contains("5 mm/s", recovery.Facts[0]);
            Assert.Contains("Good", recovery.Facts[0]);
            Assert.Equal(recovery.Facts[0], gateway.ProjectedFacts(recovery.Id)[0]);
            var finding = Assert.Single(advice.Findings, x => x.Scope == "Recovery");
            Assert.Equal(new[] { snapshot.Id, recovery.Id }, finding.EvidenceRefs);
            var expanded = (await session.Client.GetFromJsonAsync<InvestigationEvidence>($"{runPath}/evidence/{recovery.Id}"))!;
            Assert.Equal(recovery.Sha256, expanded.Sha256);
            Assert.Equal(recovery.Facts, expanded.Facts);
        }
        else
        {
            var events = Assert.Single(run.Evidence, x => x.Kind == "EventReadings");
            Assert.Contains(events.Facts, fact => fact.Contains("值 5 mm/s，质量 Good", StringComparison.Ordinal));
            var postTriggerLowAt = highAt.AddSeconds(-1).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
            Assert.Contains(gateway.ProjectedFacts(events.Id), fact =>
                fact?.Contains(postTriggerLowAt, StringComparison.Ordinal) == true
                && fact.Contains("值 5 mm/s，质量 Good", StringComparison.Ordinal));
            Assert.DoesNotContain(run.Evidence, x => x.Kind == "RecoveryReadings");
            Assert.DoesNotContain(advice.Findings, x => x.Scope == "Recovery");
        }

        using var proposed = await session.Client.PostAsync(runPath + "/proposals", null);
        Assert.True(proposed.IsSuccessStatusCode, await proposed.Content.ReadAsStringAsync());
        var proposal = (await proposed.Content.ReadFromJsonAsync<ActionProposalDto>())!;
        Assert.Equal("Pending", proposal.Status);
        var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
        Assert.False(await db.WorkOrders.AnyAsync());
        var confirmation = new ConfirmActionProposal(proposal.Version, proposal.PayloadHash, "p6-recovery-fixture");
        using var confirmed = await session.Client.PostAsJsonAsync($"/api/v1/action-proposals/{proposal.Id}/execute", confirmation);
        Assert.True(confirmed.IsSuccessStatusCode, await confirmed.Content.ReadAsStringAsync());
        var receipt = (await confirmed.Content.ReadFromJsonAsync<ActionProposalDto>())!;
        var order = await db.WorkOrders.SingleAsync();
        Assert.Equal(receipt.ResultEntityId, order.Id);
        Assert.Equal(alarm.Id, order.AlarmId);
        Assert.Equal(proposal.Id, order.AiProposalId);
        Assert.Equal("Draft", order.Status);
        var reread = (await session.Client.GetFromJsonAsync<AlarmDto>(alarmPath))!;
        Assert.Equal(selected.State, reread.State);
        Assert.Equal(selected.RecoveredAt, reread.RecoveredAt);
        Assert.Null(reread.AcknowledgedAt);
    }

    // The scripted response exercises references and workflow. A reviewer evaluates interpretation separately.
    private sealed class MeasurementReadGateway : ILlmGateway
    {
        private JsonElement[] finalEvidence = [];

        public JsonElement ProjectedEvidence(string id) =>
            Assert.Single(finalEvidence, x => x.GetProperty("id").GetString() == id);

        public string?[] ProjectedFacts(string id) =>
            ProjectedEvidence(id).GetProperty("facts").EnumerateArray().Select(x => x.GetString()).ToArray();

        public Task<LlmCompletion> CompleteAsync(LlmRequest request, LlmBudget budget, CancellationToken cancellationToken = default)
        {
            using var input = JsonDocument.Parse(request.Messages.Single(x => x.Role == LlmRole.User).Content!);
            if (input.RootElement.GetProperty("toolOutcomes").GetArrayLength() == 0)
            {
                return new ScriptedLlmGateway([new(null, [new("read", "get_recent_measurements", "{}")], "fixture", new(100, 20))])
                    .CompleteAsync(request, budget, cancellationToken);
            }

            finalEvidence = input.RootElement.GetProperty("evidence").EnumerateArray().Select(x => x.Clone()).ToArray();
            var advice = FakeLlmGateway.SimulateAdvice(input.RootElement);
            var recovery = finalEvidence.SingleOrDefault(x => x.GetProperty("kind").GetString() == "RecoveryReadings");
            if (recovery.ValueKind != JsonValueKind.Undefined)
            {
                var alarm = Assert.Single(finalEvidence, x => x.GetProperty("kind").GetString() == "Alarm");
                var finding = new InvestigationFinding("F-recovery", [recovery.GetProperty("subjectRef").GetString()!],
                    "Recovery", "EquipmentCondition", "Inconclusive",
                    "告警快照记录了恢复，原始节选只支持核查所列观测。",
                    [alarm.GetProperty("id").GetString()!, recovery.GetProperty("id").GetString()!]);
                advice = advice with
                {
                    Findings = [.. advice.Findings, finding]
                };
            }

            return new ScriptedLlmGateway([new(InvestigationAdviceContract.Serialize(advice), [], "fixture", new(100, 50))])
                .CompleteAsync(request, budget, cancellationToken);
        }
    }

    [DatabaseScenarioTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScriptedOptionalReadIsCorrelatedWithFrozenEvidenceAndPendingProposal(bool includeEmpty)
    {
        var gateway = new ReadThenFinishGateway(includeEmpty);
        var clock = new AlarmTests.Clock();
        await using var session = new AssetManagementTests.Session(clock, await TestDatabase.IsolatedConnection(), configureServices: services =>
            services.AddSingleton<ILlmGateway>(gateway));
        await session.Initialize();
        await session.Login();
        var fixture = await AlarmTests.Configure(session);
        var alarm = await AlarmTests.Raise(session, fixture, clock);
        var started = await session.Client.PostAsync($"/api/v1/alarms/{alarm.Id}/investigations", null);
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var run = (await started.Content.ReadFromJsonAsync<InvestigationRunDto>())!;
        await using var scope = session.Factory.Services.CreateAsyncScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<IInvestigationService>().ProcessNextAsync());
        run = (await session.Client.GetFromJsonAsync<InvestigationRunDto>($"/api/v1/investigations/{run.Id}"))!;
        Assert.Equal("Succeeded", run.Status);
        var advice = InvestigationAdviceContract.ParseAndValidate(run.Result!.Value.GetRawText(), run.Evidence);
        var traces = run.RunRecord!.Value.GetProperty("toolTrace").EnumerateArray().ToArray();
        Assert.Equal("get_asset_details", traces[0].GetProperty("name").GetString());
        Assert.Empty(AgentEvidenceAcceptance.Check(gateway.Choices, traces.Skip(1).ToArray(), run.Evidence, advice));
        if (includeEmpty)
        {
            Assert.Contains(run.Evidence, x => x.Kind == "Maintenance" && x.State?.Availability == "Empty");
        }
        var proposed = await session.Client.PostAsync($"/api/v1/investigations/{run.Id}/proposals", null);
        Assert.Equal(HttpStatusCode.OK, proposed.StatusCode);
        var proposal = (await proposed.Content.ReadFromJsonAsync<ActionProposalDto>())!;
        Assert.Equal("Pending", proposal.Status);
        Assert.NotNull(proposal.Draft);
        Assert.Null(proposal.ResultEntityId);
        var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
        Assert.Empty(await db.WorkOrders.ToListAsync());
    }

    // Scripted selection and demo final content verify plumbing only, never real autonomy or semantics.
    private sealed class ReadThenFinishGateway(bool includeEmpty) : ILlmGateway
    {
        public LlmToolCall[] Choices => includeEmpty
            ? [new("read", "get_recent_alarms", "{}"), new("empty", "get_maintenance_history", "{}")]
            : [new("read", "get_recent_alarms", "{}")];

        private int round;

        public Task<LlmCompletion> CompleteAsync(LlmRequest request, LlmBudget budget, CancellationToken cancellationToken = default)
        {
            LlmCompletion completion;
            if (round++ == 0)
            {
                completion = new(null, Choices, "script", new(100, 20));
            }
            else
            {
                using var input = JsonDocument.Parse(request.Messages.First(x => x.Role == LlmRole.User).Content!);
                var advice = FakeLlmGateway.SimulateAdvice(input.RootElement);
                var id = input.RootElement.GetProperty("toolOutcomes")[0].GetProperty("evidenceIds")[0].GetString()!;
                advice = advice with
                {
                    Recommendations = [advice.Recommendations[0] with { EvidenceRefs = [.. advice.Recommendations[0].EvidenceRefs, id] }, .. advice.Recommendations.Skip(1)]
                };
                completion = new(InvestigationAdviceContract.Serialize(advice), [], "script", new(100, 50));
            }
            return new ScriptedLlmGateway([completion]).CompleteAsync(request, budget, cancellationToken);
        }
    }

    [DatabaseFact]
    public async Task FailedValidationPersistsRuleAndBranchWithoutPublishingRejectedContent()
    {
        var clock = new AlarmTests.Clock();
        await using var session = new AssetManagementTests.Session(clock, await TestDatabase.IsolatedConnection(), configureServices: services =>
            services.AddSingleton<ILlmGateway>(new RejectedHypothesisGateway()));
        await session.Initialize();
        await session.Login();
        var fixture = await AlarmTests.Configure(session);
        var alarm = await AlarmTests.Raise(session, fixture, clock);
        var response = await session.Client.PostAsync($"/api/v1/alarms/{alarm.Id}/investigations", null);
        var run = (await response.Content.ReadFromJsonAsync<InvestigationRunDto>())!;
        await using var scope = session.Factory.Services.CreateAsyncScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<IInvestigationService>().ProcessNextAsync());
        var raw = await session.Client.GetStringAsync($"/api/v1/investigations/{run.Id}");
        run = JsonSerializer.Deserialize<InvestigationRunDto>(raw, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("Failed", run.Status);
        Assert.Equal("InvalidResponse", run.FailureCode);
        Assert.Null(run.Result);
        var diagnostics = run.RunRecord!.Value.GetProperty("diagnostics").EnumerateArray().ToArray();
        var rejected = Assert.Single(diagnostics, x => x.GetProperty("stage").GetString() == "advice-validation");
        Assert.Equal("advice-hypothesis-basis", rejected.GetProperty("details").GetProperty("validationRule").GetString());
        Assert.Equal("overlapping-support-opposition", rejected.GetProperty("details").GetProperty("validationDetails").GetProperty("branch").GetString());
        Assert.NotEmpty(run.Evidence);
        Assert.DoesNotContain("PRIVATE_REJECTED_HYPOTHESIS", raw);
    }

    [DatabaseScenarioTheory]
    [InlineData("DraftReady", HttpStatusCode.OK)]
    [InlineData("NeedsInformation", HttpStatusCode.Conflict)]
    [InlineData("NotRecommended", HttpStatusCode.Conflict)]
    public async Task AdviceStatusControlsDraftCreationWithoutFailingTheInvestigation(string draftStatus, HttpStatusCode expected)
    {
        var clock = new AlarmTests.Clock();
        await using var session = new AssetManagementTests.Session(clock, await TestDatabase.IsolatedConnection(), configureServices: services =>
            services.AddSingleton<ILlmGateway>(new AdviceGateway(draftStatus)));
        await session.Initialize();
        await session.Login();
        var fixture = await AlarmTests.Configure(session);
        var alarm = await AlarmTests.Raise(session, fixture, clock);
        var started = await session.Client.PostAsync($"/api/v1/alarms/{alarm.Id}/investigations", null);
        var run = (await started.Content.ReadFromJsonAsync<InvestigationRunDto>())!;
        await using var scope = session.Factory.Services.CreateAsyncScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<IInvestigationService>().ProcessNextAsync());
        run = (await session.Client.GetFromJsonAsync<InvestigationRunDto>($"/api/v1/investigations/{run.Id}"))!;
        Assert.Equal("Succeeded", run.Status);
        var diagnostics = run.RunRecord!.Value.GetProperty("diagnostics").EnumerateArray().ToArray();
        Assert.Contains(diagnostics, x => x.GetProperty("stage").GetString() == "advice-validation"
            && x.GetProperty("outcome").GetString() == "Accepted");
        Assert.Contains(diagnostics, x => x.GetProperty("stage").GetString() == "read-tool");
        Assert.DoesNotContain(diagnostics, x => x.GetProperty("details").ValueKind == JsonValueKind.Object
            && x.GetProperty("details").TryGetProperty("content", out _));
        Assert.Equal("NeedsMoreEvidence", run.Result!.Value.GetProperty("status").GetString());
        Assert.Equal(draftStatus, run.Result.Value.GetProperty("workOrder").GetProperty("status").GetString());
        var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
        Assert.Empty(await db.WorkOrders.Where(x => x.AssetId == fixture.Asset.Id).ToListAsync());
        var response = await session.Client.PostAsync($"/api/v1/investigations/{run.Id}/proposals", null);
        Assert.True(expected == response.StatusCode, response.StatusCode + ":" + await response.Content.ReadAsStringAsync());
        if (draftStatus == "DraftReady")
        {
            var proposal = (await response.Content.ReadFromJsonAsync<ActionProposalDto>())!;
            var plan = run.Result.Value.GetProperty("workOrder").GetProperty("draft");
            Assert.Equal(plan.GetProperty("title").GetString(), proposal.Draft!.Title);
            Assert.Equal("High", proposal.Draft.Priority);
            Assert.Contains(run.Result.Value.GetProperty("recommendations")[0].GetProperty("task").GetString()!, proposal.Draft.Description);
            Assert.Null(proposal.Draft.AssignedToUserId);
        }
        else
        {
            Assert.Empty(await db.ActionProposals.Where(x => x.AiRunId == run.Id).ToListAsync());
        }
        Assert.Empty(await db.WorkOrders.Where(x => x.AssetId == fixture.Asset.Id).ToListAsync());
    }

    private sealed class RejectedHypothesisGateway : ILlmGateway
    {
        public Task<LlmCompletion> CompleteAsync(LlmRequest request, LlmBudget budget, CancellationToken cancellationToken = default)
        {
            using var input = JsonDocument.Parse(request.Messages.First(x => x.Role == LlmRole.User).Content!);
            var advice = FakeLlmGateway.SimulateAdvice(input.RootElement);
            var recommendation = advice.Recommendations[0];
            advice = advice with
            {
                Hypotheses = [new("H1", "PRIVATE_REJECTED_HYPOTHESIS", "Weak", recommendation.EvidenceRefs,
                    recommendation.EvidenceRefs, [recommendation.Id])]
            };
            return new ScriptedLlmGateway([new(InvestigationAdviceContract.Serialize(advice), [], "fixture", new(100, 40))])
                .CompleteAsync(request, budget, cancellationToken);
        }
    }

    private sealed class AdviceGateway(string status) : ILlmGateway
    {
        public Task<LlmCompletion> CompleteAsync(LlmRequest request, LlmBudget budget, CancellationToken cancellationToken = default)
        {
            using var input = JsonDocument.Parse(request.Messages.First(x => x.Role == LlmRole.User).Content!);
            var advice = FakeLlmGateway.SimulateAdvice(input.RootElement);
            var gap = new InvestigationGap("G-plan", "RequiresOnSite", "需要核实现场工况。", "限制原因判断。", [], [advice.Recommendations[0].Id]);
            advice = advice with
            {
                Status = "NeedsMoreEvidence",
                BlockingGapIds = [gap.Id],
                Gaps = [gap],
                WorkOrder = advice.WorkOrder with
                {
                    Status = status,
                    BlockingGapIds = status == "NeedsInformation" ? [gap.Id] : [],
                    Draft = status == "DraftReady" ? advice.WorkOrder.Draft! with
                    {
                        Priority = "High"
                    } : null
                }
            };
            return new ScriptedLlmGateway([new(InvestigationAdviceContract.Serialize(advice), [], "fixture", new(100, 40))])
                .CompleteAsync(request, budget, cancellationToken);
        }
    }
}
