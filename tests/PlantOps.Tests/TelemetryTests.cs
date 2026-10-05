using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PlantOps.Application.Assets;
using PlantOps.Application.Telemetry;
using PlantOps.Domain.Telemetry;
using PlantOps.Infrastructure.Persistence;
using PlantOps.Infrastructure.Telemetry;
using Xunit;

namespace PlantOps.Tests;

[Collection("Database")]
public sealed class TelemetryTests
{
    private static readonly Guid North = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid South = Guid.Parse("10000000-0000-0000-0000-000000000002");
    private static readonly Guid Pump = Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly Guid Fan = Guid.Parse("30000000-0000-0000-0000-000000000002");
    private static async Task<(DirectoryRecord Asset, PointOverview Point, SourceDto Source, MappingDto Mapping)> Configure(AssetManagementTests.Session s)
    {
        var asset = await s.Create("assets", North);
        var p = await s.Client.PostAsJsonAsync($"/api/v1/assets/{asset.Id}/measurement-points", new PointInput("VIBRATION_RMS", "Vibration RMS"));
        Assert.Equal(HttpStatusCode.Created, p.StatusCode);
        var point = (await p.Content.ReadFromJsonAsync<PointOverview>())!;
        var r = await s.Client.PostAsJsonAsync($"/api/v1/plants/{North}/data-sources", new SourceInput("Test simulation"));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var source = (await r.Content.ReadFromJsonAsync<SourceDto>())!;
        var m = await s.Client.PostAsJsonAsync($"/api/v1/assets/{asset.Id}/measurement-points/{point.Id}/source-mappings", new MappingInput(source.Id, "vibration"));
        Assert.Equal(HttpStatusCode.Created, m.StatusCode);
        return (asset, point, source, (await m.Content.ReadFromJsonAsync<MappingDto>())!);
    }
    private static async Task<SourceConfiguration> Configuration(AssetManagementTests.Session s, Guid pointId)
    {
        await using var scope = s.Factory.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<ISourceConfigurationReader>().GetEnabledAsync()).Single(x => x.PointId == pointId);
    }
    private static async Task<IngestResult> Ingest(AssetManagementTests.Session s, params NormalizedSample[] samples)
    {
        await using var scope = s.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IMeasurementIngestor>().IngestAsync(samples);
    }
    private static string Trend(Guid asset, Guid point, DateTimeOffset from, DateTimeOffset to, string bucket = "raw") =>
        $"/api/v1/assets/{asset}/measurements?pointId={point}&from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}&bucket={bucket}";

    [DatabaseFact]
    public async Task TelemetryRequiresSessionRoleScopeAndAntiforgery()
    {
        await using var s = new AssetManagementTests.Session(); await s.Initialize();
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.Client.GetAsync($"/api/v1/assets/{Pump}/measurement-points")).StatusCode);
        await s.Login();
        var config = await Configure(s);
        foreach (var path in new[] { $"/api/v1/assets/{Fan}/measurement-points", $"/api/v1/plants/{South}/data-sources", Trend(Fan, config.Point.Id, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow), $"/api/v1/assets/{Fan}/measurement-points/{config.Point.Id}/source-mappings" })
            Assert.Equal(HttpStatusCode.NotFound, (await s.Client.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Client.PostAsJsonAsync($"/api/v1/assets/{Fan}/measurement-points", new PointInput("X", "Denied"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Client.PostAsJsonAsync($"/api/v1/plants/{South}/data-sources", new SourceInput("Denied"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Client.PatchAsJsonAsync($"/api/v1/assets/{Pump}/measurement-points/{config.Point.Id}", new PointInput("X", "Wrong asset"))).StatusCode);
        s.Client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PostAsJsonAsync($"/api/v1/assets/{Pump}/measurement-points", new PointInput("X", "Denied"))).StatusCode);
        foreach (var role in new[] { "Viewer", "Operator", "MaintenanceEngineer" })
        {
            await using var reader = new AssetManagementTests.Session(); await reader.Initialize(role); await reader.Login();
            Assert.Equal(HttpStatusCode.OK, (await reader.Client.GetAsync($"/api/v1/assets/{Pump}/measurement-points")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.Client.PostAsJsonAsync($"/api/v1/assets/{Pump}/measurement-points", new PointInput("X", "Denied"))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.Client.PostAsJsonAsync($"/api/v1/plants/{North}/data-sources", new SourceInput("Denied"))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.Client.PatchAsJsonAsync($"/api/v1/assets/{config.Asset.Id}/measurement-points/{config.Point.Id}/source-mappings/{config.Mapping.Id}", new MappingInput(config.Source.Id, "vibration", Version: config.Mapping.Version))).StatusCode);
        }
    }

    [DatabaseFact]
    public async Task IngestionDeduplicatesAndKeepsObservedGoodAndHistoryIndependent()
    {
        await using var s = new AssetManagementTests.Session(); await s.Initialize(); await s.Login(); var fixture = await Configure(s);
        var c = await Configuration(s, fixture.Point.Id); var now = DateTimeOffset.UtcNow;
        var good = SamplePolicy.Normalize(c, 4, "Good", now, now, now, "good");
        var replies = await Task.WhenAll(Ingest(s, good), Ingest(s, good));
        Assert.Equal(1, replies.Sum(x => x.Inserted)); Assert.Equal(1, replies.Sum(x => x.Duplicates));
        await Ingest(s, SamplePolicy.Normalize(c, 7, "Bad", now.AddSeconds(1), null, now.AddSeconds(1), "bad"),
            SamplePolicy.Normalize(c, 6, "Uncertain", now.AddSeconds(2), null, now.AddSeconds(2), "uncertain"),
            SamplePolicy.Normalize(c, 99, "Good", now.AddMinutes(-1), null, now.AddSeconds(3), "late"),
            SamplePolicy.Normalize(c, 99, "Good", now.AddHours(1), null, now.AddSeconds(4), "future"));
        var point = (await s.Client.GetFromJsonAsync<PointOverview[]>($"/api/v1/assets/{fixture.Asset.Id}/measurement-points"))!.Single();
        Assert.Equal("Uncertain", point.LatestObserved!.Quality); Assert.False(point.LatestObserved.IsTrusted);
        Assert.Equal(4, point.LastGood!.Value); Assert.True((now.AddSeconds(4) - point.LastReceivedAt!.Value).Duration() < TimeSpan.FromMicroseconds(1));
        var trend = (await s.Client.GetFromJsonAsync<MeasurementTrend>(Trend(fixture.Asset.Id, c.PointId, now.AddMinutes(-2), now.AddHours(2))))!;
        Assert.Equal(5, trend.Samples.Count); Assert.Contains(trend.Samples, x => x.TimeAnomaly);
        Assert.Null(trend.Samples.Single(x => x.Quality == "Bad").Value);
        Assert.All(trend.Samples, x => { Assert.Equal(c.MappingId, x.SourceMappingId); Assert.Equal(c.MappingVersion, x.MappingVersion); });
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.GetAsync(Trend(fixture.Asset.Id, c.PointId, now.AddDays(-2), now))).StatusCode);
        await using var scope = s.Factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
        var reading = await db.MeasurementReadings.FirstAsync(x => x.PointId == c.PointId); reading.NumericValue = 100;
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [DatabaseFact]
    public async Task IngestionRollsBackReadingsAndProjectionAndRejectsStaleConfiguration()
    {
        await using var s = new AssetManagementTests.Session(); await s.Initialize(); await s.Login(); var f = await Configure(s);
        var c = await Configuration(s, f.Point.Id); var now = DateTimeOffset.UtcNow;
        var sample = SamplePolicy.Normalize(c, 4, "Good", now, now, now, "rollback");
        s.Failure.Enabled = true; await Assert.ThrowsAsync<InvalidOperationException>(() => Ingest(s, sample)); s.Failure.Enabled = false;
        await using (var scope = s.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
            Assert.False(await db.MeasurementReadings.AnyAsync(x => x.PointId == c.PointId));
            Assert.False(await db.PointLatestStates.AnyAsync(x => x.PointId == c.PointId));
        }
        Assert.Equal(1, (await Ingest(s, sample)).Inserted);
        Assert.Equal(HttpStatusCode.OK, (await s.Client.PatchAsJsonAsync($"/api/v1/assets/{f.Asset.Id}/measurement-points/{f.Point.Id}", new PointInput(f.Point.Code, "Changed", Version: f.Point.Version))).StatusCode);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Ingest(s, sample with { SourceEventKey = "stale-config" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Ingest(s, sample with { Configuration = c with { PlantId = South }, SourceEventKey = "cross-plant" }));
    }

    [DatabaseFact]
    public async Task ConfigurationVersionsAuditRollbackMappingRulesAndArchivePreserveHistory()
    {
        await using var s = new AssetManagementTests.Session(); await s.Initialize(both: true); await s.Login(); var f = await Configure(s);
        var c = await Configuration(s, f.Point.Id); var now = DateTimeOffset.UtcNow.AddMinutes(-1);
        await Ingest(s, SamplePolicy.Normalize(c, 4, "Good", now, null, now, "archive"));
        var pointPath = $"/api/v1/assets/{f.Asset.Id}/measurement-points/{f.Point.Id}";
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PatchAsJsonAsync(pointPath, new PointInput(f.Point.Code, f.Point.Name, CanonicalUnit: "m/s", Version: f.Point.Version))).StatusCode);
        var mPath = pointPath + "/source-mappings";
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PostAsJsonAsync(mPath, new MappingInput(f.Source.Id, "duplicate"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PatchAsJsonAsync(mPath + "/" + f.Mapping.Id, new MappingInput(f.Source.Id, "changed", Version: f.Mapping.Version))).StatusCode);
        var southResponse = await s.Client.PostAsJsonAsync($"/api/v1/plants/{South}/data-sources", new SourceInput("South simulation"));
        var southSource = (await southResponse.Content.ReadFromJsonAsync<SourceDto>())!;
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PostAsJsonAsync(mPath, new MappingInput(southSource.Id, "cross plant"))).StatusCode);
        s.Failure.Enabled = true;
        Assert.Equal(HttpStatusCode.InternalServerError, (await s.Client.PatchAsJsonAsync(pointPath, new PointInput(f.Point.Code, "Must rollback", Version: f.Point.Version))).StatusCode);
        s.Failure.Enabled = false;
        var input = new PointInput(f.Point.Code, "Committed", Version: f.Point.Version);
        var results = await Task.WhenAll(s.Client.PatchAsJsonAsync(pointPath, input), s.Client.PatchAsJsonAsync(pointPath, input));
        Assert.Single(results, x => x.StatusCode == HttpStatusCode.OK); Assert.Single(results, x => x.StatusCode == HttpStatusCode.Conflict);
        var latest = (await s.Client.GetFromJsonAsync<PointOverview[]>($"/api/v1/assets/{f.Asset.Id}/measurement-points"))!.Single();
        Assert.False(latest.LastGood!.IsFresh); Assert.False(latest.LatestObserved!.IsTrusted);
        Assert.Equal(HttpStatusCode.OK, (await s.Client.PatchAsJsonAsync($"/api/v1/assets/{f.Asset.Id}", new DirectoryInput(f.Asset.Code, f.Asset.Name, f.Asset.Version, true, LocationId: f.Asset.LocationId))).StatusCode);
        Assert.DoesNotContain(await ConfigurationList(s), x => x.PointId == c.PointId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Ingest(s, SamplePolicy.Normalize(c, 7, "Good", now, null, now, "after-archive")));
        await using var scope = s.Factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
        Assert.Single(await db.MeasurementReadings.Where(x => x.PointId == c.PointId).ToListAsync());
        Assert.False((await db.MeasurementPoints.SingleAsync(x => x.Id == c.PointId)).Enabled);
        Assert.False((await db.SourceMappings.SingleAsync(x => x.Id == c.MappingId)).Enabled);
        Assert.Equal(3, await db.AuditLogs.CountAsync(x => x.ResourceId == c.PointId));
        Assert.Equal(2, await db.AuditLogs.CountAsync(x => x.ResourceId == c.MappingId));
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PatchAsJsonAsync(pointPath, new PointInput(f.Point.Code, "Archived", Version: latest.Version + 1))).StatusCode);
        Assert.False(db.Database.HasPendingModelChanges());
    }
    private static async Task<IReadOnlyList<SourceConfiguration>> ConfigurationList(AssetManagementTests.Session s)
    { await using var scope = s.Factory.Services.CreateAsyncScope(); return await scope.ServiceProvider.GetRequiredService<ISourceConfigurationReader>().GetEnabledAsync(); }

    [DatabaseFact]
    public async Task SourceAndMappingVersionsAndRepeatedSeedPreserveEdits()
    {
        await using var s = new AssetManagementTests.Session(); await s.Initialize(); await s.Login(); var f = await Configure(s);
        var path = $"/api/v1/assets/{f.Asset.Id}/measurement-points/{f.Point.Id}/source-mappings/{f.Mapping.Id}";
        var disabled = new MappingInput(f.Source.Id, f.Mapping.SourceKey, Enabled: false, Version: f.Mapping.Version);
        Assert.Equal(HttpStatusCode.OK, (await s.Client.PatchAsJsonAsync(path, disabled)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.PatchAsJsonAsync(path, disabled)).StatusCode);
        var changed = new MappingInput(f.Source.Id, "new-key", Scale: 2, Enabled: false, Version: f.Mapping.Version + 1);
        Assert.Equal(HttpStatusCode.OK, (await s.Client.PatchAsJsonAsync(path, changed)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.Client.PatchAsJsonAsync(path, changed with { Enabled = true, Version = changed.Version + 1 })).StatusCode);
        var c = await Configuration(s, f.Point.Id); Assert.Equal(2, c.Scale); Assert.Equal("new-key", c.SourceKey);
        var sourcePath = $"/api/v1/plants/{North}/data-sources/{f.Source.Id}";
        var source = new SourceInput("Stopped", false, f.Source.Version);
        Assert.Equal(HttpStatusCode.OK, (await s.Client.PatchAsJsonAsync(sourcePath, source)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.PatchAsJsonAsync(sourcePath, source)).StatusCode);
        Assert.DoesNotContain(await ConfigurationList(s), x => x.PointId == f.Point.Id);
        await using var scope = s.Factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
        var seedId = Guid.Parse("40000000-0000-0000-0000-000000000001");
        var seed = await db.MeasurementPoints.SingleAsync(x => x.Id == seedId);
        var oldName = seed.Name; var oldEnabled = seed.Enabled;
        seed.Name = "Preserve telemetry edit"; seed.Enabled = false; await db.SaveChangesAsync();
        try
        {
            await DevelopmentDatabaseInitializer.InitializeAsync(db, "Development");
            await DevelopmentDatabaseInitializer.InitializeAsync(db, "Development"); await db.Entry(seed).ReloadAsync();
            Assert.Equal("Preserve telemetry edit", seed.Name); Assert.False(seed.Enabled);
            Assert.Single(await db.MeasurementPoints.Where(x => x.Id == seedId).ToListAsync());
            Assert.Equal(4, await db.AuditLogs.CountAsync(x => x.ResourceId == f.Mapping.Id));
            Assert.Equal(2, await db.AuditLogs.CountAsync(x => x.ResourceId == f.Source.Id));
        }
        finally { seed.Name = oldName; seed.Enabled = oldEnabled; await db.SaveChangesAsync(); }
        // The relational constraint independently rejects a foreign-plant point, even without the API.
        db.MeasurementPoints.Add(new MeasurementPoint { Id = Guid.NewGuid(), PlantId = South, AssetId = f.Asset.Id, Code = "Invalid", Name = "Invalid" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public void SimulationAndMinuteTrendsAreDeterministicAndExposeInvalidDataAndGaps()
    {
        var c = SamplePolicyTests.Configuration; var start = DateTimeOffset.FromUnixTimeSeconds(120 * 1000);
        var times = new[] { 0, 1, 30, 60, 70, 80 };
        var rows = times.Select(t => SamplePolicy.ToReading(SimulationSource.SampleAt(c, start.AddSeconds(t), start.AddSeconds(t)))).ToArray();
        Assert.Equal(4, rows[0].NumericValue); Assert.Equal(12.6, rows[2].NumericValue); Assert.Null(rows[3].NumericValue);
        Assert.Equal("Uncertain", rows[4].Quality); Assert.Equal(6, rows[5].NumericValue);
        Assert.Equal(SimulationSource.SampleAt(c, start, start), SimulationSource.SampleAt(c, start, start));
        var point = new MeasurementPoint { Id = c.PointId };
        var trend = TrendBuilder.Build(point, rows, start, start.AddMinutes(3), "1m", start.AddMinutes(3));
        Assert.Equal(3, trend.Buckets.Count); Assert.All(trend.Buckets, x => Assert.True(x.HasGap));
        Assert.Equal(4, trend.Buckets[0].Min); Assert.Equal(12.6, trend.Buckets[0].Max);
        Assert.Equal(6, trend.Buckets[1].Average); Assert.Equal(1, trend.Buckets[1].BadCount); Assert.Equal(1, trend.Buckets[1].UncertainCount);
        Assert.Null(trend.Buckets[2].Average); Assert.True(trend.TrailingGap);
        var raw = TrendBuilder.Build(point, rows, start, start.AddMinutes(3), "raw", start.AddMinutes(3));
        Assert.True(raw.Samples[2].GapBefore);
    }
}

