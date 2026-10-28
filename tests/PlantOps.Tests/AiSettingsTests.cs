using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PlantOps.AI;
using PlantOps.Application.AI;
using PlantOps.Infrastructure.Persistence;
using Xunit;

namespace PlantOps.Tests;

[Collection("Database")]
public sealed class AiSettingsTests
{
    private const string Secret = "fixture-secret-never-a-real-key";

    private static void Profile(IServiceCollection services) => services.PostConfigure<AiOptions>(options =>
    {
        options.ApiKey = Secret;
        options.Model = "fixture-model";
        options.BaseUrl = "https://example.invalid/v1";
    });

    [DatabaseFact]
    public async Task SettingsRequireAdminCsrfAndConfiguredProfileAndNeverExposeSecrets()
    {
        var connection = await TestDatabase.IsolatedConnection();
        await using var admin = new AssetManagementTests.Session(connection: connection);
        await admin.Initialize();
        Assert.Equal(HttpStatusCode.Unauthorized, (await admin.Client.GetAsync("/api/v1/settings/ai")).StatusCode);
        await admin.Login();
        var settings = (await admin.Client.GetFromJsonAsync<AiSettingsDto>("/api/v1/settings/ai"))!;
        Assert.False(settings.RealConfigured);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.Client.PutAsJsonAsync("/api/v1/settings/ai", new AiSettingsInput("OpenAiCompatible", 0))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.Client.PutAsJsonAsync("/api/v1/settings/ai", new AiSettingsInput("arbitrary-provider", 0))).StatusCode);
        admin.Client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.Client.PutAsJsonAsync("/api/v1/settings/ai", new AiSettingsInput("Fake", 0))).StatusCode);
        await using var viewer = new AssetManagementTests.Session(connection: connection);
        await viewer.Initialize("Viewer");
        await viewer.Login();
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Client.GetAsync("/api/v1/settings/ai")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Client.PutAsJsonAsync("/api/v1/settings/ai", new AiSettingsInput("Fake", 0))).StatusCode);
        await using var scope = admin.Factory.Services.CreateAsyncScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>().AiRuntimeSettings.ToArrayAsync());
    }

    [DatabaseFact]
    public async Task SavingIsAuditedVersionedAndVisibleAcrossHostsWithoutSendingARequest()
    {
        var connection = await TestDatabase.IsolatedConnection();
        await using var api = new AssetManagementTests.Session(connection: connection, configureServices: Profile);
        await api.Initialize();
        await api.Login();
        var response = await api.Client.PutAsJsonAsync("/api/v1/settings/ai", new AiSettingsInput("OpenAiCompatible", 0));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Secret, json);
        Assert.DoesNotContain("example.invalid", json);
        var saved = (await response.Content.ReadFromJsonAsync<AiSettingsDto>())!;
        Assert.True(saved.RealConfigured);
        Assert.Equal(1, saved.Version);
        Assert.Equal(HttpStatusCode.Conflict, (await api.Client.PutAsJsonAsync("/api/v1/settings/ai", new AiSettingsInput("Fake", 0))).StatusCode);
        await using var workerHost = new AssetManagementTests.Session(connection: connection, configureServices: Profile);
        await using var workerScope = workerHost.Factory.Services.CreateAsyncScope();
        var selected = await workerScope.ServiceProvider.GetRequiredService<IAiSettingsService>().SelectionAsync();
        Assert.Equal(new("OpenAiCompatible", "fixture-model"), selected);
        var db = workerScope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
        Assert.Single(await db.AuditLogs.Where(log => log.ResourceType == "AiSettings").ToArrayAsync());
        Assert.Empty(await db.AiRuns.ToArrayAsync());
    }

    private sealed class SelectionEngine(IServiceProvider services, List<string> selections) : IInvestigationEngine
    {
        public Task<InvestigationExecutionResult> ExecuteAsync(IReadOnlyList<InvestigationEvidence> evidence,
            Func<string, JsonElement, CancellationToken, Task<InvestigationToolResult>> readTool,
            Func<CancellationToken, Task> authorize, LlmBudget budget, CancellationToken ct, string language = "zh-CN")
        {
            var options = services.GetRequiredService<IOptions<AiOptions>>().Value;
            var gateway = services.GetRequiredService<ILlmGateway>();
            Assert.Equal(options.Provider == "Fake" ? typeof(FakeLlmGateway) : typeof(OpenAiCompatibleGateway), gateway.GetType());
            selections.Add(options.Provider);
            // Exercise the real selector, never the external transport.
            return Task.FromResult(new InvestigationExecutionResult("{}", options.Model, options.Provider == "Fake"));
        }
    }

    [DatabaseFact]
    public async Task QueuedRunsKeepTheirModeInBothSwitchDirections()
    {
        var selections = new List<string>();
        var clock = new AlarmTests.Clock();
        await using var session = new AssetManagementTests.Session(clock, await TestDatabase.IsolatedConnection(), services =>
        {
            Profile(services);
            services.AddTransient<IInvestigationEngine>(provider => new SelectionEngine(provider, selections));
        });
        await session.Initialize();
        await session.Login();
        var fixture = await AlarmTests.Configure(session);
        var alarm = await AlarmTests.Raise(session, fixture, clock);
        var fake = (await (await session.Client.PostAsync($"/api/v1/alarms/{alarm.Id}/investigations", null)).Content.ReadFromJsonAsync<InvestigationRunDto>())!;
        Assert.Equal("Fake", fake.Provider);
        Assert.Equal(HttpStatusCode.OK, (await session.Client.PutAsJsonAsync("/api/v1/settings/ai", new AiSettingsInput("OpenAiCompatible", 0))).StatusCode);
        await using (var scope = session.Factory.Services.CreateAsyncScope())
        {
            Assert.True(await scope.ServiceProvider.GetRequiredService<IInvestigationService>().ProcessNextAsync());
        }
        var real = (await (await session.Client.PostAsync($"/api/v1/alarms/{alarm.Id}/investigations", null)).Content.ReadFromJsonAsync<InvestigationRunDto>())!;
        Assert.Equal("OpenAiCompatible", real.Provider);
        Assert.Equal(HttpStatusCode.OK, (await session.Client.PutAsJsonAsync("/api/v1/settings/ai", new AiSettingsInput("Fake", 1))).StatusCode);
        await using (var scope = session.Factory.Services.CreateAsyncScope())
        {
            Assert.True(await scope.ServiceProvider.GetRequiredService<IInvestigationService>().ProcessNextAsync());
        }
        Assert.Equal(new[] { "Fake", "OpenAiCompatible" }, selections);
        var fakeDone = (await session.Client.GetFromJsonAsync<InvestigationRunDto>($"/api/v1/investigations/{fake.Id}"))!;
        var realDone = (await session.Client.GetFromJsonAsync<InvestigationRunDto>($"/api/v1/investigations/{real.Id}"))!;
        Assert.Equal("Succeeded", fakeDone.Status);
        Assert.True(fakeDone.Simulated);
        Assert.Equal("Succeeded", realDone.Status);
        Assert.False(realDone.Simulated);
    }

    [Fact]
    public void ExecutionOptionsAreScopedAndRealModelChangesFailClosed()
    {
        using var provider = new ServiceCollection().AddLogging().AddPlantOpsAi(new ConfigurationBuilder().Build()).BuildServiceProvider();
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();
        var selection = first.ServiceProvider.GetRequiredService<IAiRunConfiguration>();
        selection.Select("OpenAiCompatible", selection.RealModel);
        Assert.IsType<OpenAiCompatibleGateway>(first.ServiceProvider.GetRequiredService<ILlmGateway>());
        Assert.IsType<FakeLlmGateway>(second.ServiceProvider.GetRequiredService<ILlmGateway>());
        Assert.Throws<LlmException>(() => selection.Select("OpenAiCompatible", "unexpected-model"));
    }
}
