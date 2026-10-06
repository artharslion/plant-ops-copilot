using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PlantOps.Application.Assets;
using PlantOps.Application.Telemetry;
using PlantOps.Domain.Assets;
using PlantOps.Domain.Identity;
using PlantOps.Domain.Telemetry;
using PlantOps.Infrastructure.Identity;
using PlantOps.Infrastructure.Persistence;

namespace PlantOps.Infrastructure.Telemetry;

public sealed class TelemetryService(PlantOpsDbContext db, IAssetDirectory directory, UserManager<PlantOpsUser> users,
    TimeProvider clock) : ITelemetryService
{
    private async Task Admin(Actor actor)
    {
        var user = await users.FindByIdAsync(actor.UserId);
        if (user is not { IsEnabled: true }) throw new DirectoryException(401, "Session unavailable.");
        if (!await users.IsInRoleAsync(user, "Admin")) throw new DirectoryException(403, "Only Admin can configure telemetry.");
    }
    private async Task<DirectoryRecord> Asset(Actor actor, Guid id, bool write = false)
    {
        if (write) await Admin(actor);
        var asset = await directory.GetAsync(actor, "assets", id);
        if (write && (!await db.Assets.AsNoTracking().AnyAsync(x => x.Id == id && !x.IsArchived)
            || !await db.Plants.AsNoTracking().AnyAsync(x => x.Id == asset.PlantId && !x.IsArchived)))
            throw new DirectoryException(400, "Archived resources cannot be configured.");
        return asset;
    }
    private async Task<MeasurementPoint> Point(Guid assetId, Guid id) =>
        await db.MeasurementPoints.SingleOrDefaultAsync(x => x.Id == id && x.AssetId == assetId)
        ?? throw new DirectoryException(404, "Point not found.");
    private async Task Lock(Guid plantId) => await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({plantId.ToString()}, 0))");
    private PointOverview Map(MeasurementPoint point, PointLatestState? state, SourceMapping? mapping, string? kind)
    {
        var now = clock.GetUtcNow();
        ReadingSnapshot? observed = state?.LatestObservedReadingId is Guid id && state.LatestObservedEffectiveAt is DateTimeOffset at
            ? new(id, state.LatestObservedValue, state.LatestObservedQuality!, at, state.LatestObservedReceivedAt!.Value,
                SamplePolicy.IsFresh(at, now, point.StaleAfterMs), state.LatestObservedQuality == "Good" && state.LatestObservedValue.HasValue && SamplePolicy.IsFresh(at, now, point.StaleAfterMs)) : null;
        ReadingSnapshot? good = state?.LastGoodReadingId is Guid goodId && state.LastTrustedEffectiveAt is DateTimeOffset goodAt
            ? new(goodId, state.LastGoodValue, "Good", goodAt, state.LastGoodReceivedAt!.Value,
                SamplePolicy.IsFresh(goodAt, now, point.StaleAfterMs), SamplePolicy.IsFresh(goodAt, now, point.StaleAfterMs)) : null;
        return new(point.Id, point.AssetId, point.Code, point.Name, point.Quantity, point.CanonicalUnit, point.ValueType,
            point.ExpectedIntervalMs, point.StaleAfterMs, point.Enabled, point.Version, observed, good, state?.LastReceivedAt,
            mapping?.DataSourceId, mapping?.Id, kind, mapping?.SourceKey, mapping?.Version);
    }
    public async Task<IReadOnlyList<PointOverview>> PointsAsync(Actor actor, Guid assetId)
    {
        await Asset(actor, assetId);
        var points = await db.MeasurementPoints.AsNoTracking().Where(x => x.AssetId == assetId).OrderBy(x => x.Code).Take(200).ToListAsync();
        var ids = points.Select(x => x.Id).ToArray();
        var states = await db.PointLatestStates.AsNoTracking().Where(x => ids.Contains(x.PointId)).ToDictionaryAsync(x => x.PointId);
        var mappings = await db.SourceMappings.AsNoTracking().Where(x => ids.Contains(x.PointId) && x.Enabled).ToDictionaryAsync(x => x.PointId);
        var sourceIds = mappings.Values.Select(x => x.DataSourceId).ToArray();
        var sources = await db.DataSources.AsNoTracking().Where(x => sourceIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id);
        return points.Select(p => Map(p, states.GetValueOrDefault(p.Id), mappings.GetValueOrDefault(p.Id),
            mappings.TryGetValue(p.Id, out var m) ? sources.GetValueOrDefault(m.DataSourceId)?.Kind : null)).ToList();
    }
    public async Task<MeasurementTrend> TrendAsync(Actor actor, Guid assetId, Guid pointId, DateTimeOffset from, DateTimeOffset to, string bucket)
    {
        await Asset(actor, assetId); var point = await Point(assetId, pointId);
        if (from.Offset != TimeSpan.Zero || to.Offset != TimeSpan.Zero || from >= to || to - from > TimeSpan.FromHours(24)
            || bucket is not ("raw" or "1m") || bucket == "1m" && Math.Ceiling((to - from).TotalMinutes) + (from.Second > 0 || from.Millisecond > 0 ? 1 : 0) > 1440)
            throw new DirectoryException(400, "Use a UTC window up to 24h and bucket raw or 1m (at most 1440 minute buckets).");
        var gaps = await db.IngestionGaps.AsNoTracking().Where(x => x.PointId == pointId && x.From < to && (x.To == null || x.To > from)).ToListAsync();
        if (bucket == "1m")
        {
            if (from.UtcTicks % TimeSpan.TicksPerMinute != 0 || to.UtcTicks % TimeSpan.TicksPerMinute != 0 || to > MeasurementMaintenance.Minute(clock.GetUtcNow()))
                throw new DirectoryException(400, "Minute trends require complete UTC minutes ending before the current minute.");
            var saved = await db.MeasurementMinutes.AsNoTracking().Where(x => x.PointId == pointId && x.BucketStart >= from && x.BucketStart < to).ToDictionaryAsync(x => x.BucketStart);
            var dirty = (await db.DirtyMeasurementMinutes.AsNoTracking().Where(x => x.PointId == pointId && x.BucketStart >= from && x.BucketStart < to).Select(x => x.BucketStart).ToListAsync()).ToHashSet();
            var buckets = new List<TrendBucket>();
            for (var at = from; at < to; at = at.AddMinutes(1))
            {
                var m = saved.GetValueOrDefault(at);
                var explicitGap = gaps.Any(x => x.From < at.AddMinutes(1) && (x.To == null || x.To > at));
                buckets.Add(m == null || dirty.Contains(at) ? new(at, null, null, null, 0, 0, 0, 0, true, 0)
                    : new(at, m.Min, m.Max, m.Average, m.GoodCount, m.UncertainCount, m.BadCount, m.AnomalyCount, m.HasGap || explicitGap, m.CoverageRatio));
            }
            return new(pointId, point.CanonicalUnit, from, to, bucket, clock.GetUtcNow(), [], buckets, buckets[0].HasGap, buckets[^1].HasGap);
        }
        const int maxRows = 100000;
        var fromDate = DateOnly.FromDateTime(from.UtcDateTime); var toDate = DateOnly.FromDateTime(to.AddTicks(-1).UtcDateTime);
        var readings = await db.MeasurementReadings.AsNoTracking().Where(x => x.PointId == pointId && x.EffectiveDate >= fromDate && x.EffectiveDate <= toDate && x.EffectiveAt >= from && x.EffectiveAt < to)
            .OrderBy(x => x.EffectiveAt).ThenBy(x => x.ReceivedAt).ThenBy(x => x.Id).Take(maxRows + 1).ToListAsync();
        if (readings.Count > maxRows) throw new DirectoryException(400, "Too many samples. Choose a smaller window or minute buckets.");
        return TrendBuilder.Build(point, readings, from, to, bucket, clock.GetUtcNow(), gaps);
    }
    private static void Text(string? value, int max)
    { if (string.IsNullOrWhiteSpace(value) || value.Length > max) throw new DirectoryException(400, "Required text is missing or exceeds its limit."); }
    private void Audit(Actor actor, Guid plant, Guid id, string kind, string? before, object after)
    {
        db.AuditLogs.Add(new AuditLog { ActorUserId = actor.UserId, PlantId = plant, ResourceId = id, ResourceType = kind,
            Action = before == null ? "Create" : "Update", Before = before, After = JsonSerializer.Serialize(after),
            OccurredAt = clock.GetUtcNow(), CorrelationId = actor.CorrelationId });
    }
    private async Task Save()
    {
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException) { throw new DirectoryException(409, "Version conflict. Refresh configuration."); }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: "23505" })
        { throw new DirectoryException(400, "Duplicate point code or another mapping is already enabled."); }
    }
    private static void Version(long current, long requested)
    { if (current != requested) throw new DirectoryException(409, "Version conflict. Refresh configuration."); }
    public async Task<PointOverview> SavePointAsync(Actor actor, Guid assetId, Guid? pointId, PointInput input)
    {
        var asset = await Asset(actor, assetId, true);
        Text(input.Code, 64); Text(input.Name, 200); Text(input.Quantity, 64); Text(input.CanonicalUnit, 32);
        if (input.Code != input.Code.Trim() || input.ExpectedIntervalMs is < 1000 or > 60000 || input.ExpectedIntervalMs % 1000 != 0
            || input.StaleAfterMs < input.ExpectedIntervalMs || input.StaleAfterMs > 300000)
            throw new DirectoryException(400, "Intervals must be whole seconds (1–60); stale timeout must cover the interval, up to 300s.");
        await using var transaction = await db.Database.BeginTransactionAsync(); await Lock(asset.PlantId); await Asset(actor, assetId, true);
        var point = pointId.HasValue ? await Point(assetId, pointId.Value) : new MeasurementPoint { Id = Guid.NewGuid(), AssetId = assetId, PlantId = asset.PlantId };
        string? before = null;
        if (pointId.HasValue)
        {
            await db.Entry(point).ReloadAsync(); Version(point.Version, input.Version); before = JsonSerializer.Serialize(point);
            if ((point.Quantity != input.Quantity || point.CanonicalUnit != input.CanonicalUnit || point.ExpectedIntervalMs != input.ExpectedIntervalMs)
                && (await db.PointLatestStates.AnyAsync(x => x.PointId == point.Id) || await db.MeasurementMinutes.AnyAsync(x => x.PointId == point.Id)
                    || await db.MeasurementReadings.AnyAsync(x => x.PointId == point.Id) || await db.SourceMappings.AnyAsync(x => x.PointId == point.Id && x.Enabled)))
                throw new DirectoryException(400, "A point with readings or an enabled mapping cannot change quantity, unit or sampling interval. Create a new point.");
            point.Version++;
        }
        else
        {
            if (await db.MeasurementPoints.CountAsync(x => x.AssetId == assetId) >= 200) throw new DirectoryException(400, "At most 200 points per asset.");
            db.MeasurementPoints.Add(point);
        }
        point.Code = input.Code; point.Name = input.Name; point.Quantity = input.Quantity; point.CanonicalUnit = input.CanonicalUnit;
        point.ExpectedIntervalMs = input.ExpectedIntervalMs; point.StaleAfterMs = input.StaleAfterMs; point.Enabled = input.Enabled;
        Audit(actor, asset.PlantId, point.Id, "MeasurementPoint", before, point); await Save(); await transaction.CommitAsync();
        return (await PointsAsync(actor, assetId)).Single(x => x.Id == point.Id);
    }
    public async Task<IReadOnlyList<SourceDto>> SourcesAsync(Actor actor, Guid plantId)
    {
        await directory.GetAsync(actor, "plants", plantId);
        return await db.DataSources.AsNoTracking().Where(x => x.PlantId == plantId).OrderBy(x => x.Id).Take(200)
            .Select(x => new SourceDto(x.Id, x.PlantId, x.Name, x.Kind, x.Enabled, x.Version)).ToListAsync();
    }
    public async Task<SourceDto> SaveSourceAsync(Actor actor, Guid plantId, Guid? id, SourceInput input)
    {
        await Admin(actor); var plant = await directory.GetAsync(actor, "plants", plantId); Text(input.Name, 200);
        await using var transaction = await db.Database.BeginTransactionAsync(); await Lock(plantId); await Admin(actor);
        plant = await directory.GetAsync(actor, "plants", plantId);
        if (!await db.Plants.AsNoTracking().AnyAsync(x => x.Id == plantId && !x.IsArchived)) throw new DirectoryException(400, "Plant is archived.");
        var source = id.HasValue ? await db.DataSources.SingleOrDefaultAsync(x => x.Id == id && x.PlantId == plantId)
            ?? throw new DirectoryException(404, "Source not found.") : new DataSource { Id = Guid.NewGuid(), PlantId = plantId };
        string? before = null;
        if (id.HasValue) { await db.Entry(source).ReloadAsync(); Version(source.Version, input.Version); before = JsonSerializer.Serialize(source); source.Version++; }
        else
        {
            if (await db.DataSources.CountAsync(x => x.PlantId == plantId) >= 200) throw new DirectoryException(400, "At most 200 sources per plant.");
            db.DataSources.Add(source);
        }
        source.Name = input.Name; source.Enabled = input.Enabled;
        Audit(actor, plantId, source.Id, "DataSource", before, source); await Save(); await transaction.CommitAsync();
        return new(source.Id, source.PlantId, source.Name, source.Kind, source.Enabled, source.Version);
    }
    public async Task<IReadOnlyList<MappingDto>> MappingsAsync(Actor actor, Guid assetId, Guid pointId)
    {
        await Asset(actor, assetId); await Point(assetId, pointId);
        return (await db.SourceMappings.AsNoTracking().Where(x => x.PointId == pointId).OrderBy(x => x.Id).Take(200).ToListAsync()).Select(MapMapping).ToList();
    }
    private static MappingDto MapMapping(SourceMapping x) => new(x.Id, x.PlantId, x.PointId, x.DataSourceId, x.SourceKey,
        x.RawUnit, x.Scale, x.Offset, x.SourceParameters, x.Enabled, x.Version);
    public async Task<MappingDto> SaveMappingAsync(Actor actor, Guid assetId, Guid pointId, Guid? id, MappingInput input)
    {
        var asset = await Asset(actor, assetId, true); Text(input.SourceKey, 128); Text(input.RawUnit, 32);
        if (!double.IsFinite(input.Scale) || input.Scale == 0 || !double.IsFinite(input.Offset)) throw new DirectoryException(400, "Scale/offset must be finite; scale cannot be zero.");
        await using var transaction = await db.Database.BeginTransactionAsync(); await Lock(asset.PlantId); await Asset(actor, assetId, true);
        var point = await Point(assetId, pointId); await db.Entry(point).ReloadAsync();
        var source = await db.DataSources.AsNoTracking().SingleOrDefaultAsync(x => x.Id == input.DataSourceId && x.PlantId == asset.PlantId)
            ?? throw new DirectoryException(400, "Source must be in the same plant.");
        if (source.Kind != "Simulation" || point.Quantity != "Vibration" || point.CanonicalUnit != "mm/s" || input.RawUnit != "mm/s")
            throw new DirectoryException(400, "GoldenPath simulation supports Vibration in mm/s only.");
        if (input.Enabled && (!source.Enabled || !point.Enabled)) throw new DirectoryException(400, "Enable source and point before enabling a mapping.");
        var mapping = id.HasValue ? await db.SourceMappings.SingleOrDefaultAsync(x => x.Id == id && x.PointId == pointId)
            ?? throw new DirectoryException(404, "Mapping not found.") : new SourceMapping { Id = Guid.NewGuid(), PlantId = asset.PlantId, PointId = pointId };
        string? before = null;
        if (id.HasValue)
        {
            await db.Entry(mapping).ReloadAsync(); Version(mapping.Version, input.Version); before = JsonSerializer.Serialize(mapping);
            if (mapping.Enabled && (mapping.DataSourceId != input.DataSourceId || mapping.SourceKey != input.SourceKey
                || mapping.Scale != input.Scale || mapping.Offset != input.Offset || mapping.RawUnit != input.RawUnit))
                throw new DirectoryException(400, "Disable the mapping before changing its source or conversion.");
            mapping.Version++;
        }
        else
        {
            if (await db.SourceMappings.CountAsync(x => x.PointId == pointId) >= 200) throw new DirectoryException(400, "At most 200 historical mappings per point.");
            db.SourceMappings.Add(mapping);
        }
        mapping.DataSourceId = input.DataSourceId; mapping.SourceKey = input.SourceKey; mapping.RawUnit = input.RawUnit;
        mapping.Scale = input.Scale; mapping.Offset = input.Offset; mapping.Enabled = input.Enabled;
        Audit(actor, asset.PlantId, mapping.Id, "SourceMapping", before, mapping); await Save(); await transaction.CommitAsync();
        return MapMapping(mapping);
    }
}
