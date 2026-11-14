using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PlantOps.AI;
using PlantOps.Application.AI;
using PlantOps.Application.Alarms;
using PlantOps.Application.Knowledge;
using PlantOps.Infrastructure.Persistence;
using Xunit;

namespace PlantOps.Tests;

[Collection("Database")]
public sealed class InvestigationAdviceAgentLiveTests
{
    [AdviceAgentLiveFact]
    public Task RealAgentReadsScopedEvidenceAndProducesAReviewableProposal() => Run(4, "agent-work-order.json");

    [AdviceAgentLiveFact]
    public Task RealInitialSnapshotProducesAReviewableProposalWithoutExtraReads() => Run(1, "initial-snapshot-work-order.json");

    [AdviceAgentLiveFact]
    public Task RealAgentInvestigatesRecordedRecovery() => Run(4, "recorded-recovery-agent.json", true);

    [AdviceAgentLiveFact]
    public Task RealAgentInvestigatesBriefLowWithoutRecovery() => Run(4, "brief-low-agent.json", false);

    private static async Task Run(int rounds, string reportName, bool? recoveryCase = null)
    {
        var root = Environment.GetEnvironmentVariable("PLANTOPS_LIVE_REPORT_ROOT")!;
        var path = Path.Combine(root, reportName);
        Assert.False(File.Exists(path));
        var clock = new AlarmTests.Clock();
        var connection = await TestDatabase.IsolatedConnection();
        await using var session = new AssetManagementTests.Session(clock, connection, configureServices: services =>
        {
            services.PostConfigure<AiOptions>(o =>
            {
                o.Provider = "OpenAiCompatible";
                o.MaxRetries = 0;
                // Match the existing Docker workbench investigation allowances.
                o.MaxInputTokens = 32000;
                o.MaxOutputTokens = 6000;
                o.MaxModelRounds = rounds;
                o.MaxToolCalls = 8;
                o.StreamResponses = true;
                o.RequestTimeoutSeconds = 120;
                o.RunTimeoutSeconds = 180;
            });
        });
        await session.Initialize();
        await session.Login();
        var configuration = session.Factory.Services.GetRequiredService<IConfiguration>();
        configuration["AI:Provider"] = "OpenAiCompatible";
        configuration["AI:MaxInputTokens"] = "32000";
        configuration["AI:MaxOutputTokens"] = "6000";
        configuration["AI:MaxModelRounds"] = rounds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        configuration["AI:MaxToolCalls"] = "8";
        configuration["AI:RunTimeoutSeconds"] = "180";
        AlarmTests.Fixture fixture;
        AlarmDto alarm;
        if (recoveryCase is { } recovered)
        {
            var seeded = await RecoveryAlarmFixture.Create(session, clock, recovered);
            fixture = seeded.Fixture;
            alarm = (await session.Client.GetFromJsonAsync<AlarmDto>($"/api/v1/alarms/{seeded.Alarm.Id}"))!;
            Assert.Equal(seeded.RecoveryAt, alarm.RecoveredAt);
        }
        else
        {
            fixture = await AlarmTests.Configure(session);
            alarm = await AlarmTests.Raise(session, fixture, clock);
            var uploaded = await DocumentTests.Upload(session, new(fixture.Asset.PlantId, fixture.Asset.Id, "原创振动检查规程 vibration"),
            content: Encoding.UTF8.GetBytes("# 原创振动检查规程 vibration\n\n振动告警后，由维护工程师核对独立振动观测、运行负载及轴承巡检记录；记录差异和无法核实的范围。没有结果的维护摘要不能证明维修已完成。本规程只用于诊断核查，未授权设备控制。"));
            await DocumentTests.Publish(session, (await uploaded.Content.ReadFromJsonAsync<DocumentDto>())!, 1);
        }
        var response = await session.Client.PostAsync($"/api/v1/alarms/{alarm.Id}/investigations", null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var run = (await response.Content.ReadFromJsonAsync<InvestigationRunDto>())!;
        await using var scope = session.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
        var before = await db.WorkOrders.CountAsync();
        Assert.True(await scope.ServiceProvider.GetRequiredService<IInvestigationService>().ProcessNextAsync());
        run = (await session.Client.GetFromJsonAsync<InvestigationRunDto>($"/api/v1/investigations/{run.Id}"))!;
        ActionProposalDto? proposal = null;
        InvestigationAdvice? advice = null;
        string? proposalFailure = null;
        if (run.Status == "Succeeded" && run.Result != null)
        {
            advice = InvestigationAdviceContract.ParseAndValidate(run.Result.Value.GetRawText(), run.Evidence);
            if (advice.WorkOrder.Status == "DraftReady")
            {
                var proposed = await session.Client.PostAsync($"/api/v1/investigations/{run.Id}/proposals", null);
                if (proposed.StatusCode == HttpStatusCode.OK)
                {
                    proposal = await proposed.Content.ReadFromJsonAsync<ActionProposalDto>();
                }
                else
                {
                    proposalFailure = proposed.StatusCode.ToString();
                }
            }
            else
            {
                proposalFailure = advice.WorkOrder.Status + ":" + advice.WorkOrder.Reason;
            }
        }
        var after = await db.WorkOrders.CountAsync();
        var choices = run.RunRecord == null ? [] : AgentEvidenceAcceptance.CapturedChoices(
            Environment.GetEnvironmentVariable("PLANTOPS_LLM_REQUEST_LEDGER")!, run.RunRecord.Value);
        // The first trace is the service's automatic asset read, not a model decision.
        var optionalTraces = run.RunRecord?.GetProperty("toolTrace").EnumerateArray().Skip(1).ToArray() ?? [];
        var acceptanceFailures = rounds > 1
            ? AgentEvidenceAcceptance.Check(choices, optionalTraces, run.Evidence, advice)
            : choices.Length == 0 && optionalTraces.Length == 0 && run.ModelRounds == 1 ? [] : new[] { "DirectFinishNotDemonstrated" };
        if (recoveryCase is { } recorded)
        {
            var measurementKind = recorded ? "RecoveryReadings" : "EventReadings";
            var excerpts = run.Evidence.Where(x => x.Kind == measurementKind && x.ResourceId == fixture.Point.Id)
                .Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
            if (!choices.Any(x => x.Name == "get_recent_measurements") || excerpts.Count == 0)
            {
                acceptanceFailures = [.. acceptanceFailures, "RecoveryScenarioMeasurementReadNotDemonstrated"];
            }
            else if (advice?.Findings.Any(x => x.Scope is "Recovery" or "AlarmEvent" && x.EvidenceRefs.Any(excerpts.Contains)) != true)
            {
                acceptanceFailures = [.. acceptanceFailures, "RecoveryScenarioExcerptNotCited"];
            }
        }
        var passed = run.Status == "Succeeded" && !run.Simulated && proposal?.Draft != null
            && proposal.Status == "Pending" && proposal.ResultEntityId == null && proposal.ExecutedAt == null
            && proposal.ApprovalCorrelationId == null && before == 0 && after == 0 && acceptanceFailures.Length == 0
            && run.RunRecord?.GetProperty("requests").GetArrayLength() == run.ModelRounds;
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            fixtureOnly = true,
            realProvider = true,
            recoveryCase,
            selectedAlarm = alarm,
            acceptanceMode = rounds > 1 ? "AutonomousRead" : "DirectFinish",
            autonomousToolSelectionDemonstrated = rounds > 1 && acceptanceFailures.Length == 0,
            choices,
            acceptanceFailures,
            protocolObservation = false,
            source = "Normal ingestor with fictional simulation readings",
            database = new Npgsql.NpgsqlConnectionStringBuilder(connection).Database,
            schema = new Npgsql.NpgsqlConnectionStringBuilder(connection).SearchPath,
            run,
            proposal,
            proposalFailure,
            workOrdersBefore = before,
            workOrdersAfter = after,
            manualApprovalPerformed = false,
            passed,
            semanticReviewRequired = true
        }, new JsonSerializerOptions { WriteIndented = true }));
        Assert.True(run.Status == "Succeeded", run.Status + ":" + run.FailureCode);
        Assert.NotNull(proposal?.Draft);
        Assert.False(run.Simulated);
        Assert.Equal(before, after);
        Assert.True(passed, string.Join(",", acceptanceFailures) + "; proposal=" + proposal?.Status);
    }
}

public sealed class AdviceAgentLiveFactAttribute : FactAttribute
{
    public AdviceAgentLiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("PLANTOPS_ADVICE_AGENT_REAL") != "1"
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PLANTOPS_LLM_REQUEST_LEDGER"))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PLANTOPS_TEST_CONNECTION"))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PLANTOPS_LIVE_REPORT_ROOT")))
        {
            Skip = "Explicit bounded real-model agent integration on the dedicated test database only.";
        }
    }
}
