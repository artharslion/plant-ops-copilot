using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PlantOps.Application.AI;
using PlantOps.Application.Alarms;
using PlantOps.Application.Assets;
using PlantOps.Application.Knowledge;
using PlantOps.Domain.AI;
using PlantOps.Infrastructure.Identity;
using PlantOps.Infrastructure.Persistence;
using Xunit;

namespace PlantOps.Tests;

[Collection("Database")]
public sealed class InvestigationServiceTests
{
    private static async Task<(AlarmTests.Fixture Fixture, AlarmDto Alarm)> Alarm(AssetManagementTests.Session session, AlarmTests.Clock clock)
    {
        var fixture = await AlarmTests.Configure(session);
        return (fixture, await AlarmTests.Raise(session, fixture, clock));
    }

    private static async Task<InvestigationRunDto> Create(AssetManagementTests.Session session, Guid alarm)
    {
        var response = await session.Client.PostAsync($"/api/v1/alarms/{alarm}/investigations", null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<InvestigationRunDto>())!;
    }

    private static async Task Process(AssetManagementTests.Session session)
    {
        await using var scope = session.Factory.Services.CreateAsyncScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<IInvestigationService>().ProcessNextAsync());
    }

    private static Task<InvestigationRunDto?> Get(AssetManagementTests.Session session, Guid run) => session.Client.GetFromJsonAsync<InvestigationRunDto>($"/api/v1/investigations/{run}");

    [DatabaseFact]
    public async Task AlarmBusinessPathCallsGatewayPersistsEvidenceAndDoesNotWriteBusinessState()
    {
        var clock = new AlarmTests.Clock();
        var gateway = new RecordingGateway();
        await using var s = new AssetManagementTests.Session(clock, await TestDatabase.IsolatedConnection(), configureServices: services => services.AddSingleton<ILlmGateway>(gateway));
        await s.Initialize();
        await s.Login();
        var (fixture, alarm) = await Alarm(s, clock);
        var upload = await DocumentTests.Upload(s, new(fixture.Asset.PlantId, fixture.Asset.Id, "振动指南"), content: Encoding.UTF8.GetBytes("# vibration 振动\n\n可能机械松动，应由维护人员按授权规程核实。"));
        await DocumentTests.Publish(s, (await upload.Content.ReadFromJsonAsync<DocumentDto>())!, 1);
        await using var check = s.Factory.Services.CreateAsyncScope();
        var db = check.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
        var workBefore = await db.WorkOrders.CountAsync();
        var auditBefore = await db.AuditLogs.CountAsync();
        var run = await Create(s, alarm.Id);
        Assert.Equal("Pending", run.Status);
        Assert.Equal(run.Id, (await Create(s, alarm.Id)).Id);
        await Process(s);
        run = (await Get(s, run.Id))!;
        Assert.Equal("Succeeded", run.Status);
        Assert.Equal(1, gateway.Calls);
        Assert.True(run.Simulated);
        Assert.Equal("fake", run.Model);
        Assert.NotNull(run.Result);
        Assert.Contains(run.Evidence, x => x.Kind == "Alarm");
        Assert.Contains(run.Evidence, x => x.Kind == "Trend");
        Assert.Contains(run.Evidence, x => x.Kind == "Maintenance");
        Assert.Contains(run.Evidence, x => x.Kind == "Document");
        Assert.Contains(run.Evidence, x => x.Kind == "DataQuality" && x.Facts.Any(fact => fact.Contains("Simulation", StringComparison.Ordinal)));
        Assert.NotEmpty(run.Warnings);
        Assert.InRange(run.InputTokens, 1, 16000);
        Assert.InRange(run.OutputTokens, 1, 2000);
        Assert.Equal(5, run.ToolCalls);
        Assert.Equal(workBefore, await db.WorkOrders.CountAsync());
        Assert.Equal(auditBefore, await db.AuditLogs.CountAsync());
        Assert.Null((await s.Client.GetFromJsonAsync<AlarmDto>($"/api/v1/alarms/{alarm.Id}"))!.AcknowledgedAt);
        var evidence = run.Evidence.First();
        Assert.Equal(evidence.Sha256, (await s.Client.GetFromJsonAsync<InvestigationEvidence>($"/api/v1/investigations/{run.Id}/evidence/{evidence.Id}"))!.Sha256);
    }

    [DatabaseFact]
    public async Task OwnerCrossPlantAndCsrfBoundariesAreEnforced()
    {
        var clock = new AlarmTests.Clock();
        var connection = await TestDatabase.IsolatedConnection();
        await using var s = new AssetManagementTests.Session(clock, connection);
        await s.Initialize();
        await s.Login();
        var (_, alarm) = await Alarm(s, clock);
        var run = await Create(s, alarm.Id);
        await using var other = new AssetManagementTests.Session(connection: connection);
        await other.Initialize("Viewer");
        await other.Login();
        Assert.Empty((await other.Client.GetFromJsonAsync<InvestigationRunDto[]>($"/api/v1/alarms/{alarm.Id}/investigations"))!);
        foreach (var suffix in new[] { "", "/evidence/E1" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await other.Client.GetAsync($"/api/v1/investigations/{run.Id}{suffix}")).StatusCode);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await other.Client.PostAsync($"/api/v1/investigations/{run.Id}/cancel", null)).StatusCode);
        await using var south = new AssetManagementTests.Session(connection: connection);
        await south.Initialize("Viewer");
        await using (var scope = south.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
            await db.UserPlantAccess.Where(x => x.UserId == south.UserId).ExecuteDeleteAsync();
        }
        await south.Login();
        Assert.Equal(HttpStatusCode.NotFound, (await south.Client.PostAsync($"/api/v1/alarms/{alarm.Id}/investigations", null)).StatusCode);
        s.Client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PostAsync($"/api/v1/alarms/{alarm.Id}/investigations", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PostAsync($"/api/v1/investigations/{run.Id}/cancel", null)).StatusCode);
    }

    [DatabaseFact]
    public async Task WithdrawnDocumentHidesStoredResultAndAllOldEvidence()
    {
        var clock = new AlarmTests.Clock();
        await using var s = new AssetManagementTests.Session(clock, await TestDatabase.IsolatedConnection());
        await s.Initialize();
        await s.Login();
        var (fixture, alarm) = await Alarm(s, clock);
        var upload = await DocumentTests.Upload(s, new(fixture.Asset.PlantId, fixture.Asset.Id, "vibration"), content: "Original vibration evidence."u8.ToArray());
        var doc = await DocumentTests.Publish(s, (await upload.Content.ReadFromJsonAsync<DocumentDto>())!, 1);
        var run = await Create(s, alarm.Id);
        await Process(s);
        run = (await Get(s, run.Id))!;
        Assert.Equal("Succeeded", run.Status);
        Assert.Contains(run.Evidence, x => x.Kind == "Document" && x.ResourceId == doc.Id);
        await DocumentTests.Publish(s, doc, 1, true);
        var hidden = (await Get(s, run.Id))!;
        Assert.Equal("Invalidated", hidden.Status);
        Assert.Null(hidden.Result);
        Assert.Empty(hidden.Evidence);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Client.GetAsync($"/api/v1/investigations/{run.Id}/evidence/E1")).StatusCode);
        await using var scope = s.Factory.Services.CreateAsyncScope();
        Assert.NotNull((await scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>().AiRuns.AsNoTracking().SingleAsync(x => x.Id == run.Id)).ResultJson);
    }

    [DatabaseFact]
    public async Task RevokedActorCannotExecuteQueuedJobOrReadOldResult()
    {
        var clock = new AlarmTests.Clock();
        var gateway = new RecordingGateway();
        await using var s = new AssetManagementTests.Session(clock, await TestDatabase.IsolatedConnection(), configureServices: services => services.AddSingleton<ILlmGateway>(gateway));
        await s.Initialize();
        await s.Login();
        var (_, alarm) = await Alarm(s, clock);
        var run = await Create(s, alarm.Id);
        await using (var scope = s.Factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<PlantOpsUser>>();
            var user = (await users.FindByIdAsync(s.UserId))!;
            user.IsEnabled = false;
            Assert.True((await users.UpdateAsync(user)).Succeeded);
        }
        await Process(s);
        Assert.Equal(0, gateway.Calls);
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.Client.GetAsync($"/api/v1/investigations/{run.Id}")).StatusCode);
        await using var check = s.Factory.Services.CreateAsyncScope();
        var stored = await check.ServiceProvider.GetRequiredService<PlantOpsDbContext>().AiRuns.AsNoTracking().SingleAsync(x => x.Id == run.Id);
        Assert.Equal("Failed", stored.Status);
        Assert.Equal("AuthorizationChanged", stored.FailureCode);
        Assert.Null(stored.ResultJson);
    }

    [DatabaseFact]
    public async Task PendingCancellationIsDurableAndNeverCallsModel()
    {
        var clock = new AlarmTests.Clock();
        var gateway = new RecordingGateway();
        await using var s = new AssetManagementTests.Session(clock, await TestDatabase.IsolatedConnection(), configureServices: services => services.AddSingleton<ILlmGateway>(gateway));
        await s.Initialize();
        await s.Login();
        var (_, alarm) = await Alarm(s, clock);
        var run = await Create(s, alarm.Id);
        Assert.Equal(HttpStatusCode.OK, (await s.Client.PostAsync($"/api/v1/investigations/{run.Id}/cancel", null)).StatusCode);
        Assert.Equal("Cancelled", (await Get(s, run.Id))!.Status);
        await using var scope = s.Factory.Services.CreateAsyncScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<IInvestigationService>().ProcessNextAsync());
        Assert.Equal(0, gateway.Calls);
    }

    [DatabaseFact]
    public async Task RunningCancellationReachesGatewayAndCannotBeOverwrittenBySuccess()
    {
        var clock = new AlarmTests.Clock();
        var gateway = new BlockingGateway();
        await using var s = new AssetManagementTests.Session(clock, await TestDatabase.IsolatedConnection(), configureServices: services => services.AddSingleton<ILlmGateway>(gateway));
        await s.Initialize();
        await s.Login();
        var (_, alarm) = await Alarm(s, clock);
        var run = await Create(s, alarm.Id);
        var process = Process(s);
        await gateway.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("Running", (await Get(s, run.Id))!.Status);
        Assert.Equal(HttpStatusCode.OK, (await s.Client.PostAsync($"/api/v1/investigations/{run.Id}/cancel", null)).StatusCode);
        await process.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(gateway.Cancelled);
        Assert.Equal("Cancelled", (await Get(s, run.Id))!.Status);
        Assert.Null((await Get(s, run.Id))!.Result);
    }

    [DatabaseFact]
    public async Task ProviderFailureKeepsAuthorizedEvidenceAndOrdinaryBusinessReadable()
    {
        var clock = new AlarmTests.Clock();
        await using var s = new AssetManagementTests.Session(clock, await TestDatabase.IsolatedConnection(), configureServices: services => services.AddSingleton<ILlmGateway>(new UnavailableGateway()));
        await s.Initialize();
        await s.Login();
        var (_, alarm) = await Alarm(s, clock);
        var run = await Create(s, alarm.Id);
        await Process(s);
        run = (await Get(s, run.Id))!;
        Assert.Equal("Failed", run.Status);
        Assert.Equal("Unavailable", run.FailureCode);
        Assert.Null(run.Result);
        Assert.NotEmpty(run.Evidence);
        Assert.Equal(HttpStatusCode.OK, (await s.Client.GetAsync($"/api/v1/alarms/{alarm.Id}")).StatusCode);
    }

    [DatabaseFact]
    public async Task ExpiredLeaseRecoversReadOnlyJobAndOldLeaseCannotFinalize()
    {
        var clock = new AlarmTests.Clock();
        await using var s = new AssetManagementTests.Session(clock, await TestDatabase.IsolatedConnection());
        await s.Initialize();
        await s.Login();
        var (_, alarm) = await Alarm(s, clock);
        var run = await Create(s, alarm.Id);
        var oldLease = Guid.NewGuid();
        await using (var scope = s.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
            await db.AiRuns.Where(x => x.Id == run.Id).ExecuteUpdateAsync(update => update.SetProperty(x => x.Status, "Running")
                .SetProperty(x => x.LeaseId, oldLease).SetProperty(x => x.LeaseUntil, clock.Now.AddSeconds(-1)).SetProperty(x => x.Attempts, 1));
        }
        await Process(s);
        Assert.Equal("Succeeded", (await Get(s, run.Id))!.Status);
        await using var check = s.Factory.Services.CreateAsyncScope();
        var checkDb = check.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
        var stored = await checkDb.AiRuns.AsNoTracking().SingleAsync(x => x.Id == run.Id);
        Assert.Equal(2, stored.Attempts);
        Assert.NotEqual(oldLease, stored.LeaseId);
        Assert.Equal(0, await checkDb.AiRuns.Where(x => x.Id == run.Id && x.LeaseId == oldLease && x.Status == "Running")
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.Status, "Failed")));
    }

    private sealed class RecordingGateway : ILlmGateway
    {
        public int Calls
        {
            get; private set;
        }

        public Task<LlmCompletion> CompleteAsync(LlmRequest request, LlmBudget budget, CancellationToken cancellationToken = default)
        {
            Calls++;
            return new PlantOps.AI.FakeLlmGateway().CompleteAsync(request, budget, cancellationToken);
        }
    }

    private sealed class UnavailableGateway : ILlmGateway
    {
        public Task<LlmCompletion> CompleteAsync(LlmRequest request, LlmBudget budget, CancellationToken cancellationToken = default) =>
            throw new LlmException(LlmFailure.Unavailable);
    }

    private sealed class BlockingGateway : ILlmGateway
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Cancelled
        {
            get; private set;
        }

        public async Task<LlmCompletion> CompleteAsync(LlmRequest request, LlmBudget budget, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Cancelled = true;
                throw;
            }
            throw new InvalidOperationException();
        }
    }
}
