using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PlantOps.AI;
using PlantOps.Application.AI;
using PlantOps.Infrastructure.Persistence;
using Xunit;

namespace PlantOps.Tests;

// Opt-in, finite external call with fictional test data; excluded from ordinary test runs.
[Collection("Database")]
public sealed class InvestigationLiveTests
{
    [LiveInvestigationFact]
    public async Task FictionalAlarmUsesRealConfiguredGatewayOnce()
    {
        var root = Environment.GetEnvironmentVariable("PLANTOPS_LIVE_REPORT_ROOT")
            ?? throw new InvalidOperationException("A fresh local report directory is required.");
        var clock = new AlarmTests.Clock { Now = DateTimeOffset.UtcNow };
        await using var session = new AssetManagementTests.Session(clock, await TestDatabase.IsolatedConnection(),
            configureServices: services => services.PostConfigure<AiOptions>(options => options.Provider = "OpenAiCompatible"));
        await session.Initialize();
        await session.Login();
        var configuration = session.Factory.Services.GetRequiredService<IConfiguration>();
        configuration["AI:Provider"] = "OpenAiCompatible";
        var fixture = await AlarmTests.Configure(session);
        var alarm = await AlarmTests.Raise(session, fixture, clock);
        await using var scope = session.Factory.Services.CreateAsyncScope();
        Assert.IsType<OpenAiCompatibleGateway>(scope.ServiceProvider.GetRequiredService<ILlmGateway>());
        var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
        var workBefore = await db.WorkOrders.CountAsync();
        var response = await session.Client.PostAsync($"/api/v1/alarms/{alarm.Id}/investigations", null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var queued = (await response.Content.ReadFromJsonAsync<InvestigationRunDto>())!;
        Assert.Equal("OpenAiCompatible", queued.Provider);
        var started = DateTimeOffset.UtcNow;
        Assert.True(await scope.ServiceProvider.GetRequiredService<IInvestigationService>().ProcessNextAsync());
        var run = (await session.Client.GetFromJsonAsync<InvestigationRunDto>($"/api/v1/investigations/{queued.Id}"))!;
        var ordinary = await session.Client.GetAsync($"/api/v1/alarms/{alarm.Id}");
        var workAfter = await db.WorkOrders.CountAsync();
        await File.WriteAllTextAsync(Path.Combine(root, "live-business.json"), JsonSerializer.Serialize(new
        {
            run.Id,
            run.Status,
            run.Provider,
            run.Model,
            run.Simulated,
            run.FailureCode,
            run.ModelRounds,
            run.ToolCalls,
            InputTokenReservation = run.InputTokens,
            OutputTokenReservation = run.OutputTokens,
            EvidenceCount = run.Evidence.Count,
            DurationSeconds = (DateTimeOffset.UtcNow - started).TotalSeconds,
            FixtureOnly = true,
            RealProviderVerified = run.Status == "Succeeded" && !run.Simulated,
            QualityAcceptance = false,
            OrdinaryAlarmReadable = ordinary.StatusCode == HttpStatusCode.OK,
            NoWorkOrderWrite = workBefore == workAfter,
            EstimatedCost = (decimal?)null
        }, new JsonSerializerOptions { WriteIndented = true }));
        Assert.False(run.Simulated);
        Assert.Equal(HttpStatusCode.OK, ordinary.StatusCode);
        Assert.Equal(workBefore, workAfter);
        Assert.Equal("Succeeded", run.Status);
        Assert.NotNull(run.Result);
    }
}

public sealed class LiveInvestigationFactAttribute : FactAttribute
{
    public LiveInvestigationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("PLANTOPS_LIVE_INVESTIGATION") != "1"
            || Environment.GetEnvironmentVariable("AI__Provider") != "OpenAiCompatible"
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PLANTOPS_TEST_CONNECTION")))
        {
            Skip = "Explicit finite real-provider investigation in a dedicated test database only.";
        }
    }
}
