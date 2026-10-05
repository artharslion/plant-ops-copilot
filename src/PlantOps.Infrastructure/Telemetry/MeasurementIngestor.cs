using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PlantOps.Application.Telemetry;
using PlantOps.Domain.Telemetry;
using PlantOps.Infrastructure.Persistence;
using PlantOps.Infrastructure.Alarms;

namespace PlantOps.Infrastructure.Telemetry;

public sealed class SourceConfigurationReader(PlantOpsDbContext db) : ISourceConfigurationReader
{
    public async Task<IReadOnlyList<SourceConfiguration>> GetEnabledAsync(CancellationToken cancellationToken = default) =>
        await (from mapping in db.SourceMappings.AsNoTracking()
            join source in db.DataSources on mapping.DataSourceId equals source.Id
            join point in db.MeasurementPoints on mapping.PointId equals point.Id
            join asset in db.Assets on point.AssetId equals asset.Id
            join plant in db.Plants on point.PlantId equals plant.Id
            where mapping.Enabled && source.Enabled && point.Enabled && !asset.IsArchived && !plant.IsArchived
            select new SourceConfiguration(point.PlantId, point.AssetId, point.Id, point.Version,
                point.CanonicalUnit, point.ExpectedIntervalMs, point.StaleAfterMs, source.Id, source.Version, source.Kind,
                mapping.Id, mapping.Version, mapping.SourceKey, mapping.RawUnit, mapping.Scale, mapping.Offset, mapping.SourceParameters))
            .ToListAsync(cancellationToken);
}

public sealed class MeasurementIngestor(PlantOpsDbContext db, TimeProvider clock) : IMeasurementIngestor
{
    public async Task<IngestResult> IngestAsync(IReadOnlyList<NormalizedSample> samples, CancellationToken cancellationToken = default)
    {
        if (samples.Count > 500) throw new ArgumentException("A measurement batch cannot exceed 500 samples.");
        if (samples.Count == 0) return new(0, 0);
        foreach (var sample in samples)
        {
            if (string.IsNullOrWhiteSpace(sample.SourceEventKey) || sample.SourceEventKey.Length > 200
                || sample.RawValueJson.Length > 8000 || sample.Quality is not ("Good" or "Uncertain" or "Bad")
                || sample.NumericValue.HasValue && !double.IsFinite(sample.NumericValue.Value)
                || sample.Quality == "Bad" && sample.NumericValue.HasValue || sample.StatusSymbol?.Length > 128
                || sample.ReceivedAt.Offset != TimeSpan.Zero || sample.SourceTimestamp?.Offset != TimeSpan.Zero && sample.SourceTimestamp.HasValue
                || sample.ServerTimestamp?.Offset != TimeSpan.Zero && sample.ServerTimestamp.HasValue)
                throw new ArgumentException("Invalid normalized sample.");
            using var raw = JsonDocument.Parse(sample.RawValueJson);
        }
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        foreach (var plant in samples.Select(x => x.Configuration.PlantId).Distinct().OrderBy(x => x))
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({plant.ToString()}, 0))", cancellationToken);
        var current = await new SourceConfigurationReader(db).GetEnabledAsync(cancellationToken);
        foreach (var c in samples.Select(x => x.Configuration).Distinct())
            if (!current.Contains(c)) throw new InvalidOperationException("Measurement configuration changed or is outside its plant/asset. Batch not committed.");
        var pointIds = samples.Select(x => x.Configuration.PointId).Distinct().ToArray();
        var keys = samples.Select(x => x.SourceEventKey).Distinct().ToArray();
        var existing = (await db.MeasurementReadings.AsNoTracking()
            .Where(x => pointIds.Contains(x.PointId) && keys.Contains(x.SourceEventKey))
            .Select(x => new { x.PointId, x.SourceEventKey }).ToListAsync(cancellationToken))
            .Select(x => (x.PointId, x.SourceEventKey)).ToHashSet();
        var states = await db.PointLatestStates.Where(x => pointIds.Contains(x.PointId)).ToDictionaryAsync(x => x.PointId, cancellationToken);
        var alarms = new AlarmBatchEvaluator(db, clock);
        await alarms.LoadAsync(pointIds, cancellationToken);
        var correlation = "ingest-" + Guid.NewGuid().ToString("N");
        // Reset tracked snapshots after acquiring the lock if this scope was used before.
        foreach (var state in states.Values) await db.Entry(state).ReloadAsync(cancellationToken);
        var inserted = 0;
        foreach (var sample in samples)
        {
            var c = sample.Configuration;
            if (!states.TryGetValue(c.PointId, out var state))
            { state = new PointLatestState { PointId = c.PointId }; states.Add(c.PointId, state); db.PointLatestStates.Add(state); }
            if (!existing.Add((c.PointId, sample.SourceEventKey)))
            {
                if (sample.ReceivedAt > state.LastReceivedAt) { state.LastReceivedAt = sample.ReceivedAt; state.Version++; }
                continue;
            }
            var reading = SamplePolicy.ToReading(sample);
            db.MeasurementReadings.Add(reading);
            SamplePolicy.Apply(state, reading, c.StaleAfterMs); state.Version++; inserted++;
            await alarms.ApplyAsync(reading, c, correlation, cancellationToken);
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(inserted, samples.Count - inserted);
    }
}
