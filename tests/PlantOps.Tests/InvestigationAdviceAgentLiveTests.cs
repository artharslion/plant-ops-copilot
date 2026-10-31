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

    private static async Task Run(int rounds, string reportName)
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
        var fixture = await AlarmTests.Configure(session);
        var alarm = await AlarmTests.Raise(session, fixture, clock);
        var uploaded = await DocumentTests.Upload(session, new(fixture.Asset.PlantId, fixture.Asset.Id, "原创振动检查规程 vibration"),
            content: Encoding.UTF8.GetBytes("# 原创振动检查规程 vibration\n\n振动告警后，由维护工程师核对独立振动观测、运行负载及轴承巡检记录；记录差异和无法核实的范围。没有结果的维护摘要不能证明维修已完成。本规程只用于诊断核查，未授权设备控制。"));
        await DocumentTests.Publish(session, (await uploaded.Content.ReadFromJsonAsync<DocumentDto>())!, 1);
        var response = await session.Client.PostAsync($"/api/v1/alarms/{alarm.Id}/investigations", null);
        var run = (await response.Content.ReadFromJsonAsync<InvestigationRunDto>())!;
        await using var scope = session.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
        var before = await db.WorkOrders.CountAsync(x => x.AssetId == fixture.Asset.Id);
        Assert.True(await scope.ServiceProvider.GetRequiredService<IInvestigationService>().ProcessNextAsync());
        run = (await session.Client.GetFromJsonAsync<InvestigationRunDto>($"/api/v1/investigations/{run.Id}"))!;
        ActionProposalDto? proposal = null;
        string? proposalFailure = null;
        if (run.Status == "Succeeded" && run.Result != null)
        {
            var advice = InvestigationAdviceContract.ParseAndValidate(run.Result.Value.GetRawText(), run.Evidence);
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
        var after = await db.WorkOrders.CountAsync(x => x.AssetId == fixture.Asset.Id);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            fixtureOnly = true,
            realProvider = true,
            autonomousToolSelectionTested = rounds > 1,
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
            passed = run.Status == "Succeeded" && proposal?.Draft != null && before == after
        }, new JsonSerializerOptions { WriteIndented = true }));
        Assert.True(run.Status == "Succeeded", run.Status + ":" + run.FailureCode);
        Assert.NotNull(proposal?.Draft);
        Assert.False(run.Simulated);
        Assert.Equal(before, after);
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
