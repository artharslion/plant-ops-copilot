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
    [DatabaseFact]
    public async Task ScriptedOptionalReadIsCorrelatedWithFrozenEvidenceAndPendingProposal()
    {
        var gateway = new ReadThenFinishGateway();
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
    private sealed class ReadThenFinishGateway : ILlmGateway
    {
        public LlmToolCall[] Choices { get; } = [new("read", "get_recent_alarms", "{}")];

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
