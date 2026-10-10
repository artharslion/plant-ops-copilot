using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PlantOps.Application.Alarms;
using PlantOps.Application.Assets;
using PlantOps.Application.Telemetry;
using PlantOps.Domain.Alarms;
using PlantOps.Infrastructure.Persistence;
using Xunit;

namespace PlantOps.Tests;

[Collection("Database")]
public sealed class AlarmTests
{
    private static readonly Guid North = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid South = Guid.Parse("10000000-0000-0000-0000-000000000002");
    private static readonly Guid Fan = Guid.Parse("30000000-0000-0000-0000-000000000002");

    internal sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    internal sealed record Fixture(DirectoryRecord Asset, PointOverview Point, RuleDto Rule, SourceConfiguration Config)
    {
        public string RulePath => $"/api/v1/assets/{Asset.Id}/measurement-points/{Point.Id}/alarm-rules";
    }

    internal static async Task<Fixture> Configure(AssetManagementTests.Session s, RuleInput? rule = null)
    {
        var asset = await s.Create("assets", North);
        var p = await s.Client.PostAsJsonAsync($"/api/v1/assets/{asset.Id}/measurement-points", new PointInput("VIB", "Vibration"));
        Assert.Equal(HttpStatusCode.Created, p.StatusCode);
        var point = (await p.Content.ReadFromJsonAsync<PointOverview>())!;
        var sourceResponse = await s.Client.PostAsJsonAsync($"/api/v1/plants/{North}/data-sources", new SourceInput("Alarm test source"));
        var source = (await sourceResponse.Content.ReadFromJsonAsync<SourceDto>())!;
        var mappingResponse = await s.Client.PostAsJsonAsync($"/api/v1/assets/{asset.Id}/measurement-points/{point.Id}/source-mappings", new MappingInput(source.Id, "vibration"));
        Assert.Equal(HttpStatusCode.Created, mappingResponse.StatusCode);
        var r = await s.Client.PostAsJsonAsync($"/api/v1/assets/{asset.Id}/measurement-points/{point.Id}/alarm-rules", rule ?? new RuleInput("High vibration"));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var saved = (await r.Content.ReadFromJsonAsync<RuleDto>())!;
        await using var scope = s.Factory.Services.CreateAsyncScope();
        var c = (await scope.ServiceProvider.GetRequiredService<ISourceConfigurationReader>().GetEnabledAsync()).Single(x => x.PointId == point.Id);
        return new(asset, point, saved, c);
    }

    private static readonly DateTimeOffset At = new(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);

    private static async Task Send(AssetManagementTests.Session s, Fixture f, Clock clock, int t, double value = 12.6, string quality = "Good", string? key = null, int delay = 0)
    {
        clock.Now = At.AddSeconds(t + delay);
        await using var scope = s.Factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IMeasurementIngestor>().IngestAsync([
            SamplePolicy.Normalize(f.Config, value, quality, At.AddSeconds(t), null, At.AddSeconds(t + delay), key ?? t.ToString())]);
    }

    private static async Task<AlarmDto[]> Alarms(AssetManagementTests.Session s, Fixture f, bool? active = null) =>
        (await s.Client.GetFromJsonAsync<AlarmPage>($"/api/v1/alarms?plantId={North}&assetId={f.Asset.Id}{(active.HasValue ? "&active=" + active.Value.ToString().ToLowerInvariant() : "")}"))!.Items.ToArray();

    internal static async Task<AlarmDto> Raise(AssetManagementTests.Session s, Fixture f, Clock clock)
    {
        for (var t = 0; t <= 3; t++)
        {
            await Send(s, f, clock, t);
        }

        return Assert.Single(await Alarms(s, f, true));
    }

    [DatabaseFact]
    public async Task ExpiredTriggerReadingIsExplicitWhileAlarmSnapshotAndEventsSurvive()
    {
        var clock = new Clock();
        await using var s = new AssetManagementTests.Session(clock, await TestDatabase.IsolatedConnection());
        await s.Initialize();
        await s.Login();
        var f = await Configure(s);
        var alarm = await Raise(s, f, clock);
        Assert.True(alarm.TriggerReadingAvailable);
        clock.Now = At.AddDays(8);
        await using (var scope = s.Factory.Services.CreateAsyncScope())
        {
            var maintenance = scope.ServiceProvider.GetRequiredService<IMeasurementMaintenance>();
            await maintenance.AggregateAsync();
            Assert.True((await maintenance.RetainAsync()).DroppedPartitions > 0);
        }
        await s.Login();
        var retained = (await s.Client.GetFromJsonAsync<AlarmDto>($"/api/v1/alarms/{alarm.Id}"))!;
        Assert.False(retained.TriggerReadingAvailable);
        Assert.Equal(alarm.TriggerValue, retained.TriggerValue);
        Assert.Equal(alarm.ThresholdSnapshot, retained.ThresholdSnapshot);
        Assert.Equal("Active", retained.State);
        Assert.Contains((await s.Client.GetFromJsonAsync<AlarmEventDto[]>($"/api/v1/alarms/{alarm.Id}/events"))!, x => x.EventType == "Raised" && x.ReadingId == alarm.TriggerReadingId);
    }

    [DatabaseFact]
    public async Task IngestionRaisesOnceAcknowledgesIndependentlyAndRecoversOnlyFromTrustedLowDuration()
    {
        var clock = new Clock();
        await using var s = new AssetManagementTests.Session(clock);
        await s.Initialize();
        await s.Login();
        var f = await Configure(s);
        await Send(s, f, clock, 0);
        await Send(s, f, clock, 1);
        await Send(s, f, clock, 2);
        Assert.Empty(await Alarms(s, f));
        await Send(s, f, clock, 3);
        var alarm = Assert.Single(await Alarms(s, f, true));
        Assert.True(alarm.CurrentObservationTrusted);
        await Send(s, f, clock, 3);
        await Send(s, f, clock, 4);
        Assert.Single(await Alarms(s, f));
        var ack = await s.Client.PostAsJsonAsync($"/api/v1/alarms/{alarm.Id}/acknowledge", new AcknowledgeInput(alarm.Version));
        Assert.Equal(HttpStatusCode.OK, ack.StatusCode);
        var acknowledged = (await ack.Content.ReadFromJsonAsync<AlarmDto>())!;
        Assert.Equal("Active", acknowledged.State);
        Assert.Null(acknowledged.RecoveredAt);
        Assert.Equal(s.UserId, acknowledged.AcknowledgedByUserId);
        await Send(s, f, clock, 5, 6, "Bad");
        await Send(s, f, clock, 6, 6, "Uncertain");
        var bad = Assert.Single(await Alarms(s, f, true));
        Assert.False(bad.CurrentObservationTrusted);
        await Send(s, f, clock, 1, 4, key: "late-low");
        Assert.Equal("Active", Assert.Single(await Alarms(s, f)).State);
        for (var t = 7; t <= 11; t++)
        {
            await Send(s, f, clock, t, 6);
        }

        Assert.Single(await Alarms(s, f, true));
        await Send(s, f, clock, 12, 6);
        var recovered = Assert.Single(await Alarms(s, f, false));
        Assert.Equal(acknowledged.AcknowledgedAt, recovered.AcknowledgedAt);
        Assert.Equal(At.AddSeconds(12), recovered.RecoveredAt);
        Assert.Contains("\"RaiseAbove\": 10", recovered.ThresholdSnapshot);
        var events = (await s.Client.GetFromJsonAsync<AlarmEventDto[]>($"/api/v1/alarms/{alarm.Id}/events"))!;
        Assert.Equal(new[] { "Raised", "Acknowledged", "Recovered" }, events.Select(x => x.EventType));
        await using var scope = s.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
        Assert.Equal(3, await db.AuditLogs.CountAsync(x => x.ResourceId == alarm.Id));
        var evt = await db.AlarmEvents.FirstAsync(x => x.AlarmId == alarm.Id);
        evt.EventType = "Changed";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [DatabaseFact]
    public async Task AlarmScopeRolesAntiforgeryAndRevocationsAreEnforcedByBackend()
    {
        var clock = new Clock();
        await using var admin = new AssetManagementTests.Session(clock);
        await admin.Initialize();
        await admin.Login();
        var f = await Configure(admin);
        var alarm = await Raise(admin, f, clock);
        await using var anonymous = new AssetManagementTests.Session();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.Client.GetAsync($"/api/v1/alarms?plantId={North}")).StatusCode);
        foreach (var path in new[] { $"/api/v1/alarms?plantId={South}", $"/api/v1/alarms?plantId={North}&assetId={Fan}", $"/api/v1/assets/{Fan}/measurement-points/{f.Point.Id}/alarm-rules" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await admin.Client.GetAsync(path)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await admin.Client.PostAsJsonAsync($"/api/v1/assets/{Fan}/measurement-points/{f.Point.Id}/alarm-rules", new RuleInput("Denied"))).StatusCode);
        foreach (var role in new[] { "Viewer", "Operator", "MaintenanceEngineer" })
        {
            await using var reader = new AssetManagementTests.Session(clock);
            await reader.Initialize(role);
            await reader.Login();
            Assert.Equal(HttpStatusCode.OK, (await reader.Client.GetAsync($"/api/v1/alarms/{alarm.Id}")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.Client.PostAsJsonAsync(f.RulePath, new RuleInput("Denied"))).StatusCode);
            Assert.Equal(role == "Viewer" ? HttpStatusCode.Forbidden : HttpStatusCode.OK,
                (await reader.Client.PostAsJsonAsync($"/api/v1/alarms/{alarm.Id}/acknowledge", new AcknowledgeInput(alarm.Version))).StatusCode);
        }
        admin.Client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.Client.PostAsJsonAsync($"/api/v1/alarms/{alarm.Id}/acknowledge", new AcknowledgeInput(1))).StatusCode);
        await admin.RefreshToken();
        await using var scope = admin.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
        var grant = await db.UserPlantAccess.SingleAsync(x => x.UserId == admin.UserId && x.PlantId == North);
        db.UserPlantAccess.Remove(grant);
        await db.SaveChangesAsync();
        foreach (var path in new[] { $"/api/v1/alarms/{alarm.Id}", $"/api/v1/alarms/{alarm.Id}/events", $"/api/v1/alarms?plantId={North}" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await admin.Client.GetAsync(path)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await admin.Client.PostAsJsonAsync($"/api/v1/alarms/{alarm.Id}/acknowledge", new AcknowledgeInput(1))).StatusCode);
    }

    [DatabaseFact]
    public async Task ConcurrentAcknowledgementsAreIdempotentAndFailureRollsBackWithAudit()
    {
        var clock = new Clock();
        await using var s = new AssetManagementTests.Session(clock);
        await s.Initialize();
        await s.Login();
        var f = await Configure(s);
        var alarm = await Raise(s, f, clock);
        var path = $"/api/v1/alarms/{alarm.Id}/acknowledge";
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.PostAsJsonAsync(path, new AcknowledgeInput(0))).StatusCode);
        s.Failure.Enabled = true;
        Assert.Equal(HttpStatusCode.InternalServerError, (await s.Client.PostAsJsonAsync(path, new AcknowledgeInput(alarm.Version))).StatusCode);
        s.Failure.Enabled = false;
        Assert.Null((await s.Client.GetFromJsonAsync<AlarmDto>($"/api/v1/alarms/{alarm.Id}"))!.AcknowledgedAt);
        await using var other = new AssetManagementTests.Session(clock);
        await other.Initialize("Operator");
        await other.Login();
        var replies = await Task.WhenAll(s.Client.PostAsJsonAsync(path, new AcknowledgeInput(alarm.Version)), other.Client.PostAsJsonAsync(path, new AcknowledgeInput(alarm.Version)));
        Assert.All(replies, x => Assert.Equal(HttpStatusCode.OK, x.StatusCode));
        var first = (await replies[0].Content.ReadFromJsonAsync<AlarmDto>())!;
        var second = (await replies[1].Content.ReadFromJsonAsync<AlarmDto>())!;
        Assert.Equal(first.AcknowledgedByUserId, second.AcknowledgedByUserId);
        Assert.Equal(first.AcknowledgedAt, second.AcknowledgedAt);
        Assert.Equal(HttpStatusCode.OK, (await s.Client.PostAsJsonAsync(path, new AcknowledgeInput(0))).StatusCode);
        await using var scope = s.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
        Assert.Equal(1, await db.AlarmEvents.CountAsync(x => x.AlarmId == alarm.Id && x.EventType == "Acknowledged"));
        Assert.Equal(2, await db.AuditLogs.CountAsync(x => x.ResourceId == alarm.Id));
    }

    [DatabaseFact]
    public async Task IngestionFailureRollsBackReadingProjectionRuleAlarmEventAndAudit()
    {
        var clock = new Clock();
        await using var s = new AssetManagementTests.Session(clock);
        await s.Initialize();
        await s.Login();
        var f = await Configure(s);
        for (var t = 0; t < 3; t++)
        {
            await Send(s, f, clock, t);
        }

        s.Failure.Enabled = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Send(s, f, clock, 3));
        s.Failure.Enabled = false;
        await using (var scope = s.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
            Assert.Empty(await db.Alarms.Where(x => x.PointId == f.Point.Id).ToListAsync());
            Assert.Equal(3, await db.MeasurementReadings.CountAsync(x => x.PointId == f.Point.Id));
            Assert.Equal(At.AddSeconds(2), (await db.PointLatestStates.SingleAsync(x => x.PointId == f.Point.Id)).LatestObservedEffectiveAt);
            Assert.Equal(At.AddSeconds(2), (await db.RuleRuntimeStates.SingleAsync(x => x.RuleId == f.Rule.Id)).LastEvaluatedAt);
            Assert.DoesNotContain(await db.AuditLogs.Where(x => x.ResourceType == "Alarm" && x.PlantId == North).Select(x => x.After).ToListAsync(), x => x.Contains(f.Point.Id.ToString()));
        }
        await Task.WhenAll(Send(s, f, clock, 3), Send(s, f, clock, 3));
        Assert.Single(await Alarms(s, f, true));
    }

    [DatabaseFact]
    public async Task ActiveAlarmBlocksRuleChangesAndArchiveWhileRecoveredHistoryKeepsThresholdSnapshot()
    {
        var clock = new Clock();
        await using var s = new AssetManagementTests.Session(clock);
        await s.Initialize();
        await s.Login();
        var f = await Configure(s);
        var alarm = await Raise(s, f, clock);
        var edit = new RuleInput("Changed", RaiseAbove: 20, Enabled: false, Version: f.Rule.Version);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PatchAsJsonAsync(f.RulePath + "/" + f.Rule.Id, edit)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PatchAsJsonAsync($"/api/v1/assets/{f.Asset.Id}", new DirectoryInput(f.Asset.Code, f.Asset.Name, f.Asset.Version, true, LocationId: f.Asset.LocationId))).StatusCode);
        for (var t = 4; t <= 9; t++)
        {
            await Send(s, f, clock, t, 6);
        }

        Assert.Equal(HttpStatusCode.OK, (await s.Client.PatchAsJsonAsync(f.RulePath + "/" + f.Rule.Id, edit)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.PatchAsJsonAsync(f.RulePath + "/" + f.Rule.Id, edit)).StatusCode);
        var history = Assert.Single(await Alarms(s, f, false));
        Assert.Equal(alarm.ThresholdSnapshot, history.ThresholdSnapshot);
        Assert.Equal(1, history.RuleVersion);
        Assert.Equal(HttpStatusCode.OK, (await s.Client.PostAsJsonAsync($"/api/v1/alarms/{alarm.Id}/acknowledge", new AcknowledgeInput(history.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.Client.PatchAsJsonAsync($"/api/v1/assets/{f.Asset.Id}", new DirectoryInput(f.Asset.Code, f.Asset.Name, f.Asset.Version, true, LocationId: f.Asset.LocationId))).StatusCode);
        Assert.Single(await Alarms(s, f));
    }

    [DatabaseFact]
    public async Task RuntimeRestartAndSourceConfigurationChangeResetTimersButKeepActiveAlarm()
    {
        var clock = new Clock();
        await using var s = new AssetManagementTests.Session(clock);
        await s.Initialize();
        await s.Login();
        var f = await Configure(s);
        await Send(s, f, clock, 0);
        await Send(s, f, clock, 1);
        await using (var scope = s.Factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IAlarmRuntimeReset>().ResetAsync(default);
        }

        await Send(s, f, clock, 2);
        await Send(s, f, clock, 3);
        Assert.Empty(await Alarms(s, f));
        var p = await s.Client.PatchAsJsonAsync($"/api/v1/assets/{f.Asset.Id}/measurement-points/{f.Point.Id}", new PointInput(f.Point.Code, "Config change", Version: f.Point.Version));
        Assert.Equal(HttpStatusCode.OK, p.StatusCode);
        await using (var scope = s.Factory.Services.CreateAsyncScope())
        {
            var c = (await scope.ServiceProvider.GetRequiredService<ISourceConfigurationReader>().GetEnabledAsync()).Single(x => x.PointId == f.Point.Id);
            f = f with
            {
                Config = c
            };
        }
        for (var t = 4; t <= 6; t++)
        {
            await Send(s, f, clock, t);
        }

        Assert.Empty(await Alarms(s, f));
        Assert.Equal(HttpStatusCode.OK, (await s.Client.PatchAsJsonAsync($"/api/v1/plants/{North}/data-sources/{f.Config.DataSourceId}", new SourceInput("Source config changed", Version: f.Config.SourceVersion))).StatusCode);
        await using (var scope = s.Factory.Services.CreateAsyncScope())
        {
            f = f with
            {
                Config = (await scope.ServiceProvider.GetRequiredService<ISourceConfigurationReader>().GetEnabledAsync()).Single(x => x.PointId == f.Point.Id)
            };
        }

        for (var t = 7; t <= 9; t++)
        {
            await Send(s, f, clock, t);
        }

        Assert.Empty(await Alarms(s, f));
        await Send(s, f, clock, 10);
        var alarm = Assert.Single(await Alarms(s, f, true));
        await Send(s, f, clock, 11, 6);
        await Send(s, f, clock, 12, 6);
        await using (var scope = s.Factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IAlarmRuntimeReset>().ResetAsync(default);
        }

        var afterRestart = Assert.Single(await Alarms(s, f, true));
        Assert.Equal(alarm.Id, afterRestart.Id);
        Assert.False(afterRestart.CurrentObservationTrusted);
        for (var t = 13; t <= 17; t++)
        {
            await Send(s, f, clock, t, 6);
        }

        Assert.Single(await Alarms(s, f, true));
        await Send(s, f, clock, 18, 6);
        Assert.Single(await Alarms(s, f, false));
    }

    [DatabaseFact]
    public async Task FastRecoveryReraiseBatchPaginationConstraintsAndSeedRemainConsistent()
    {
        var clock = new Clock();
        await using var s = new AssetManagementTests.Session(clock);
        await s.Initialize();
        await s.Login();
        var f = await Configure(s, new RuleInput("Instant", RaiseForMs: 0, ClearForMs: 0));
        await Send(s, f, clock, 0);
        clock.Now = At.AddSeconds(2);
        await using (var scope = s.Factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IMeasurementIngestor>().IngestAsync([
                SamplePolicy.Normalize(f.Config, 6, "Good", At.AddSeconds(1), null, At.AddSeconds(1), "clear"),
                SamplePolicy.Normalize(f.Config, 12.6, "Good", At.AddSeconds(2), null, At.AddSeconds(2), "raise-again")]);
        }
        Assert.Equal(2, (await Alarms(s, f)).Length);
        Assert.Single(await Alarms(s, f, true));
        var page = (await s.Client.GetFromJsonAsync<AlarmPage>($"/api/v1/alarms?plantId={North}&assetId={f.Asset.Id}&limit=1"))!;
        Assert.Single(page.Items);
        Assert.NotNull(page.NextCursor);
        var next = (await s.Client.GetFromJsonAsync<AlarmPage>($"/api/v1/alarms?plantId={North}&assetId={f.Asset.Id}&limit=1&cursor={Uri.EscapeDataString(page.NextCursor!)}"))!;
        Assert.Single(next.Items);
        Assert.NotEqual(page.Items[0].Id, next.Items[0].Id);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.GetAsync($"/api/v1/alarms?plantId={North}&limit=201")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.GetAsync($"/api/v1/alarms?plantId={North}&cursor=invalid")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PostAsJsonAsync(f.RulePath, new RuleInput("Invalid", ClearBelow: 11))).StatusCode);
        await using var inspect = s.Factory.Services.CreateAsyncScope();
        var db = inspect.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
        var seedId = Guid.Parse("70000000-0000-0000-0000-000000000001");
        var seed = await db.AlarmRules.SingleAsync(x => x.Id == seedId);
        var name = seed.Name;
        var enabled = seed.Enabled;
        seed.Name = "Preserve rule edit";
        seed.Enabled = false;
        await db.SaveChangesAsync();
        try
        {
            await DevelopmentDatabaseInitializer.InitializeAsync(db, "Development");
            await db.Entry(seed).ReloadAsync();
            Assert.Equal("Preserve rule edit", seed.Name);
            Assert.False(seed.Enabled);
        }
        finally
        {
            seed.Name = name;
            seed.Enabled = enabled;
            await db.SaveChangesAsync();
        }
        Assert.False(db.Database.HasPendingModelChanges());
        db.AlarmRules.Add(new AlarmRule { Id = Guid.NewGuid(), PlantId = South, PointId = f.Point.Id, Name = "Invalid plant" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
