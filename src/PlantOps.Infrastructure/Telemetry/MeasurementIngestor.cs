using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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
                   mapping.Id, mapping.Version, mapping.SourceKey, mapping.RawUnit, mapping.Scale, mapping.Offset, mapping.SourceParameters,
                   source.ConnectionParameters))
            .ToListAsync(cancellationToken);
}

public sealed class MeasurementIngestor(PlantOpsDbContext db, TimeProvider clock, IConfiguration? settings = null) : IMeasurementIngestor
{
    public async Task<IngestResult> IngestAsync(IReadOnlyList<NormalizedSample> samples, CancellationToken cancellationToken = default)
    {
        if (samples.Count > 500)
        {
            throw new ArgumentException("A measurement batch cannot exceed 500 samples.");
        }

        if (samples.Count == 0)
        {
            return new(0, 0);
        }

        foreach (var sample in samples)
        {
            if (string.IsNullOrWhiteSpace(sample.SourceEventKey) || sample.SourceEventKey.Length > 200
                || sample.RawValueJson.Length > 8000 || sample.Quality is not ("Good" or "Uncertain" or "Bad")
                || sample.NumericValue.HasValue && !double.IsFinite(sample.NumericValue.Value)
                || sample.Quality == "Bad" && sample.NumericValue.HasValue || sample.StatusSymbol?.Length > 128
                || sample.ReceivedAt.Offset != TimeSpan.Zero || sample.SourceTimestamp?.Offset != TimeSpan.Zero && sample.SourceTimestamp.HasValue
                || sample.ServerTimestamp?.Offset != TimeSpan.Zero && sample.ServerTimestamp.HasValue)
            {
                throw new ArgumentException("Invalid normalized sample.");
            }

            using var raw = JsonDocument.Parse(sample.RawValueJson);
        }
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await MeasurementMaintenance.LockAsync(db, cancellationToken);
        var dates = samples.Select(x => DateOnly.FromDateTime((x.SourceTimestamp ?? x.ServerTimestamp ?? x.ReceivedAt).UtcDateTime)).Distinct().ToArray();
        var rawDays = settings?.GetValue<int?>("Telemetry:RawRetentionDays") ?? 7;
        if (rawDays is < 1 or > 365)
        {
            throw new InvalidOperationException("Invalid raw retention period.");
        }

        var cutoff = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime).AddDays(-rawDays);
        if (dates.Any(x => x < cutoff))
        {
            throw new ArgumentException("Sample is outside raw retention; retained aggregates cannot be rewritten.");
        }

        if (dates.Any(x => x > DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime).AddDays(1)))
        {
            throw new ArgumentException("Sample timestamp is beyond the allowed future partition window.");
        }

        foreach (var day in dates)
        {
            var start = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
            var end = start.AddDays(1);
            if (!await db.MeasurementPartitions.AnyAsync(x => x.Date == day, cancellationToken)
                && await db.MeasurementMinutes.AnyAsync(x => x.BucketStart >= start && x.BucketStart < end, cancellationToken))
            {
                throw new ArgumentException("Raw partition was already retired; changing retention cannot rewrite historical aggregates.");
            }
        }
        var partitions = await MeasurementMaintenance.EnsureAsync(db, dates, cancellationToken);
        foreach (var plant in samples.Select(x => x.Configuration.PlantId).Distinct().OrderBy(x => x))
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({plant.ToString()}, 0))", cancellationToken);
        }

        var current = await new SourceConfigurationReader(db).GetEnabledAsync(cancellationToken);
        foreach (var c in samples.Select(x => x.Configuration).Distinct())
        {
            if (!current.Contains(c))
            {
                throw new InvalidOperationException("Measurement configuration changed or is outside its plant/asset. Batch not committed.");
            }
        }

        var pointIds = samples.Select(x => x.Configuration.PointId).Distinct().ToArray();
        var keys = samples.Select(x => x.SourceEventKey).Distinct().ToArray();
        var existing = (await db.MeasurementReadings.AsNoTracking()
            .Where(x => dates.Contains(x.EffectiveDate) && pointIds.Contains(x.PointId) && keys.Contains(x.SourceEventKey))
            .Select(x => new { x.EffectiveDate, x.PointId, x.SourceEventKey }).ToListAsync(cancellationToken))
            .Select(x => (x.EffectiveDate, x.PointId, x.SourceEventKey)).ToHashSet();
        var states = await db.PointLatestStates.Where(x => pointIds.Contains(x.PointId)).ToDictionaryAsync(x => x.PointId, cancellationToken);
        var alarms = new AlarmBatchEvaluator(db, clock);
        await alarms.LoadAsync(pointIds, cancellationToken);
        var earliest = states.Values.Where(x => x.LastTrustedEffectiveAt.HasValue).Select(x => x.LastTrustedEffectiveAt!.Value)
            .Concat(samples.Select(x => x.SourceTimestamp ?? x.ServerTimestamp ?? x.ReceivedAt)).Min();
        var latest = samples.Max(x => x.SourceTimestamp ?? x.ServerTimestamp ?? x.ReceivedAt);
        var gaps = await db.IngestionGaps.Where(x => pointIds.Contains(x.PointId) && x.From <= latest && (x.To == null || x.To >= earliest)).ToListAsync(cancellationToken);
        var profiles = await db.SamplingProfiles.AsNoTracking().Where(x => pointIds.Contains(x.PointId)
            && x.EffectiveFrom <= latest && (x.EffectiveTo == null || x.EffectiveTo >= earliest)).ToListAsync(cancellationToken);
        var correlation = "ingest-" + Guid.NewGuid().ToString("N");
        // Reset tracked snapshots after acquiring the lock if this scope was used before.
        foreach (var state in states.Values)
        {
            await db.Entry(state).ReloadAsync(cancellationToken);
        }

        var inserted = 0;
        var added = new List<MeasurementReading>();
        foreach (var sample in samples)
        {
            var c = sample.Configuration;
            if (!states.TryGetValue(c.PointId, out var state))
            {
                state = new PointLatestState { PointId = c.PointId };
                states.Add(c.PointId, state);
                db.PointLatestStates.Add(state);
            }
            var reading = SamplePolicy.ToReading(sample);
            if (!existing.Add((reading.EffectiveDate, c.PointId, sample.SourceEventKey)))
            {
                if (sample.ReceivedAt > state.LastReceivedAt)
                {
                    state.LastReceivedAt = sample.ReceivedAt;
                    state.Version++;
                }
                continue;
            }
            if (!reading.TimeAnomaly && state.LatestObservedEffectiveAt is DateTimeOffset prior
                && reading.EffectiveAt > prior)
            {
                if (c.SourceKind == "OpcUa")
                {
                    // Preserve conflicting point history until after segmentation; identity filtering
                    // must not turn overlapping/unknown history into a known periodic segment.
                    var history = profiles.Where(x => x.PointId == c.PointId).ToArray();
                    foreach (var evidence in SamplingCoverage.FindIntervalGapEvidence(prior, reading.EffectiveAt, history)
                        .Where(x => x.SourceMappingId == c.MappingId && x.MappingVersion == c.MappingVersion))
                    {
                        // The whole observed empty segment is possible loss evidence, not a first
                        // missing sample timestamp or a physical loss count.
                        var gap = new IngestionGap
                        {
                            PointId = c.PointId,
                            DataSourceId = c.DataSourceId,
                            From = evidence.From,
                            To = evidence.To,
                            DetectedAt = reading.ReceivedAt,
                            Reason = "PossibleSamplingIntervalGap",
                            IsPossible = true
                        };
                        db.IngestionGaps.Add(gap);
                        gaps.Add(gap);
                    }
                }
                else if (c.SourceKind == "Simulation"
                    && reading.EffectiveAt - prior > TimeSpan.FromMilliseconds(c.ExpectedIntervalMs * 1.5))
                {
                    var gap = new IngestionGap
                    {
                        PointId = c.PointId,
                        DataSourceId = c.DataSourceId,
                        From = prior.AddMilliseconds(c.ExpectedIntervalMs),
                        To = reading.EffectiveAt,
                        DetectedAt = reading.ReceivedAt
                    };
                    db.IngestionGaps.Add(gap);
                    gaps.Add(gap);
                }
            }
            if (SamplePolicy.HasOverflow(reading.RawStatusCode))
            {
                var overflow = new IngestionGap
                {
                    PointId = c.PointId,
                    DataSourceId = c.DataSourceId,
                    From = reading.EffectiveAt,
                    To = reading.EffectiveAt.AddTicks(1),
                    DetectedAt = reading.ReceivedAt,
                    Reason = "ServerOverflow",
                    IsPossible = true
                };
                db.IngestionGaps.Add(overflow);
                gaps.Add(overflow);
            }

            added.Add(reading);
            db.MeasurementReadings.Add(reading);
            var priorTrusted = state.LastTrustedEffectiveAt;
            SamplePolicy.Apply(state, reading, c.StaleAfterMs);
            state.Version++;
            inserted++;
            // Persisted or newly inferred loss invalidates continuity in this transaction.
            if (gaps.Any(x => x.PointId == c.PointId && x.From <= reading.EffectiveAt
                && (x.To == null || x.To > reading.EffectiveAt || priorTrusted.HasValue && x.To > priorTrusted)))
            {
                await alarms.ResetPointAsync(c.PointId, reading.EffectiveAt, cancellationToken);
            }

            foreach (var gap in gaps.Where(x => x.PointId == c.PointId && x.DataSourceId == c.DataSourceId && x.To == null && x.From <= reading.ReceivedAt))
            {
                gap.To = reading.ReceivedAt;
                gap.RecoveredAt = reading.ReceivedAt;
            }
            await alarms.ApplyAsync(reading, c, correlation, cancellationToken);
        }
        foreach (var day in added.Select(x => x.EffectiveDate).Distinct())
        {
            partitions[day].Revision++;
        }

        await db.SaveChangesAsync(cancellationToken);
        await MeasurementMaintenance.DirtyAsync(db, added, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(inserted, samples.Count - inserted);
    }
}
