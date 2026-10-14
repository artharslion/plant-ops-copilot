using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using PlantOps.Application.Telemetry;
using PlantOps.Domain.Identity;
using PlantOps.Domain.Telemetry;
using PlantOps.Infrastructure.Persistence;

namespace PlantOps.Infrastructure.Telemetry;

public sealed class MeasurementMaintenance(PlantOpsDbContext db, TimeProvider clock, IConfiguration config) : IMeasurementMaintenance
{
    internal static DateTimeOffset Minute(DateTimeOffset at) => new(at.Year, at.Month, at.Day, at.Hour, at.Minute, 0, TimeSpan.Zero);

    internal static async Task LockAsync(PlantOpsDbContext db, CancellationToken ct) => await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7300170)", ct);

    internal static string Table(DateOnly day) => "MeasurementReadings_" + day.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    internal static async Task<Dictionary<DateOnly, MeasurementPartition>> EnsureAsync(PlantOpsDbContext db, DateOnly[] dates, CancellationToken ct)
    {
        var rows = await db.MeasurementPartitions.Where(x => dates.Contains(x.Date)).ToDictionaryAsync(x => x.Date, ct);
        foreach (var row in rows.Values)
        {
            await db.Entry(row).ReloadAsync(ct);
        }

        foreach (var day in dates.Where(x => !rows.ContainsKey(x)))
        {
            var from = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var to = day.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            // PostgreSQL DDL identifiers/bounds cannot be parameters; every interpolated value comes from a typed DateOnly in invariant numeric format.
#pragma warning disable EF1002
            await db.Database.ExecuteSqlRawAsync($"CREATE TABLE \"{Table(day)}\" PARTITION OF \"MeasurementReadings\" FOR VALUES FROM ('{from}') TO ('{to}')", ct);
#pragma warning restore EF1002
            var row = new MeasurementPartition { Date = day };
            rows.Add(day, row);
            db.MeasurementPartitions.Add(row);
        }
        return rows;
    }

    internal static async Task DirtyAsync(PlantOpsDbContext db, IReadOnlyList<MeasurementReading> rows, CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return;
        }

        var json = JsonSerializer.Serialize(rows.Select(x => new { x.PointId, BucketStart = Minute(x.EffectiveAt) }).Distinct());
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"DirtyMeasurementMinutes\" (\"PointId\", \"BucketStart\") SELECT \"PointId\", \"BucketStart\" FROM jsonb_to_recordset({json}::jsonb) AS t(\"PointId\" uuid, \"BucketStart\" timestamptz) ON CONFLICT DO NOTHING", ct);
    }

    public async Task<int> AggregateAsync(CancellationToken cancellationToken = default)
    {
        var end = Minute(clock.GetUtcNow());
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockAsync(db, cancellationToken);
        var count = await db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH samples AS (
              SELECT r.*, d."BucketStart", p."ExpectedIntervalMs",
                lag(r."EffectiveAt") OVER (PARTITION BY r."PointId", d."BucketStart" ORDER BY r."EffectiveAt", r."ReceivedAt", r."Id") AS prior,
                (r."Quality" = 'Good' AND r."NumericValue" IS NOT NULL AND NOT r."TimeAnomaly"
                  AND (coalesce(r."RawStatusCode",0) & 3200) <> 1152) AS trusted
              FROM "DirtyMeasurementMinutes" d JOIN "MeasurementPoints" p ON p."Id" = d."PointId"
              JOIN "MeasurementReadings" r ON r."PointId" = d."PointId" AND r."EffectiveDate" = (d."BucketStart" AT TIME ZONE 'UTC')::date
                AND r."EffectiveAt" >= d."BucketStart" AND r."EffectiveAt" < d."BucketStart" + interval '1 minute'
              WHERE d."BucketStart" < {end}
            ), stats AS (
              SELECT "PointId", "BucketStart", min("NumericValue") FILTER (WHERE trusted) AS minimum,
                max("NumericValue") FILTER (WHERE trusted) AS maximum, avg("NumericValue") FILTER (WHERE trusted) AS average,
                count(*) FILTER (WHERE trusted)::int AS good, count(*) FILTER (WHERE "Quality" = 'Uncertain')::int AS uncertain,
                count(*) FILTER (WHERE "Quality" = 'Bad')::int AS bad, count(*) FILTER (WHERE "TimeAnomaly")::int AS anomaly,
                count(*)::int AS total, ceil(60000.0 / max("ExpectedIntervalMs")) AS expected,
                coalesce(bool_or(extract(epoch FROM ("EffectiveAt" - prior))*1000 > "ExpectedIntervalMs"*1.5),false)
                  OR extract(epoch FROM (min("EffectiveAt") - "BucketStart"))*1000 > max("ExpectedIntervalMs")*1.5
                  OR extract(epoch FROM ("BucketStart" + interval '1 minute' - max("EffectiveAt")))*1000 > max("ExpectedIntervalMs")*1.5 AS timegap
              FROM samples GROUP BY "PointId", "BucketStart"
            ) INSERT INTO "MeasurementMinutes" ("PointId","BucketStart","Min","Max","Average","GoodCount","UncertainCount","BadCount","AnomalyCount","SampleCount","HasGap","CoverageRatio")
            SELECT "PointId","BucketStart",minimum,maximum,average,good,uncertain,bad,anomaly,total,(good < expected OR timegap OR EXISTS (
              SELECT 1 FROM "IngestionGaps" g WHERE g."PointId" = stats."PointId" AND g."From" < stats."BucketStart" + interval '1 minute'
                AND (g."To" IS NULL OR g."To" > stats."BucketStart"))),least(1.0,good/expected) FROM stats
            ON CONFLICT ("PointId","BucketStart") DO UPDATE SET "Min"=EXCLUDED."Min","Max"=EXCLUDED."Max","Average"=EXCLUDED."Average",
              "GoodCount"=EXCLUDED."GoodCount","UncertainCount"=EXCLUDED."UncertainCount","BadCount"=EXCLUDED."BadCount","AnomalyCount"=EXCLUDED."AnomalyCount",
              "SampleCount"=EXCLUDED."SampleCount","HasGap"=EXCLUDED."HasGap","CoverageRatio"=EXCLUDED."CoverageRatio"
            """, cancellationToken);
        // OPC UA coverage must use recorded server revisions, never current point requests.
        // Unknown or change-driven intervals cannot establish a physical sample count.
        var protocolMinutes = await (from dirty in db.DirtyMeasurementMinutes
                                     join reading in db.MeasurementReadings on dirty.PointId equals reading.PointId
                                     where dirty.BucketStart < end && reading.SourceKind == "OpcUa"
                                         && reading.EffectiveDate == DateOnly.FromDateTime(dirty.BucketStart.UtcDateTime)
                                         && reading.EffectiveAt >= dirty.BucketStart && reading.EffectiveAt < dirty.BucketStart.AddMinutes(1)
                                     select new
                                     {
                                         dirty.PointId,
                                         dirty.BucketStart
                                     }).Distinct().ToListAsync(cancellationToken);
        foreach (var protocolMinute in protocolMinutes)
        {
            var minute = await db.MeasurementMinutes.SingleAsync(x => x.PointId == protocolMinute.PointId
                && x.BucketStart == protocolMinute.BucketStart, cancellationToken);
            await db.Entry(minute).ReloadAsync(cancellationToken);
            var from = protocolMinute.BucketStart;
            var to = from.AddMinutes(1);
            var profiles = await db.SamplingProfiles.AsNoTracking().Where(x => x.PointId == protocolMinute.PointId
                && x.EffectiveFrom < to && (x.EffectiveTo == null || x.EffectiveTo > from)).ToListAsync(cancellationToken);
            var coverage = SamplingCoverage.Calculate(from, to, minute.GoodCount, profiles);
            minute.CoverageRatio = coverage.CoverageRatio;
            minute.SamplingMode = coverage.Mode;
            var explicitGap = await db.IngestionGaps.AnyAsync(x => x.PointId == protocolMinute.PointId
                && x.From < to && (x.To == null || x.To > from), cancellationToken);
            var observations = await db.MeasurementReadings.AsNoTracking().Where(x => x.PointId == protocolMinute.PointId
                && x.EffectiveDate == DateOnly.FromDateTime(from.UtcDateTime) && x.EffectiveAt >= from && x.EffectiveAt < to)
                .ToListAsync(cancellationToken);
            minute.HasGap = coverage.Mode == "Unknown" || coverage.CoverageRatio < 1 || minute.UncertainCount > 0 || minute.BadCount > 0
                || minute.AnomalyCount > 0 || explicitGap || SamplingCoverage.HasTimingGap(from, to, profiles, observations);
        }
        await db.SaveChangesAsync(cancellationToken);
        await db.DirtyMeasurementMinutes.Where(x => x.BucketStart < end).ExecuteDeleteAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("""
            UPDATE "MeasurementPartitions" p SET "AggregatedRevision" = "Revision"
            WHERE NOT EXISTS (SELECT 1 FROM "DirtyMeasurementMinutes" d WHERE (d."BucketStart" AT TIME ZONE 'UTC')::date = p."Date")
            """, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return count;
    }

    public async Task<RetentionResult> RetainAsync(CancellationToken cancellationToken = default)
    {
        var rawDays = config.GetValue("Telemetry:RawRetentionDays", 7);
        var minuteDays = config.GetValue("Telemetry:MinuteRetentionDays", 90);
        if (rawDays is < 1 or > 365 || minuteDays < rawDays || minuteDays > 3650)
        {
            throw new InvalidOperationException("Invalid retention configuration.");
        }

        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var cutoff = today.AddDays(-rawDays);
        var minuteCutoff = new DateTimeOffset(today.AddDays(-minuteDays).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockAsync(db, cancellationToken);
        var candidates = await db.MeasurementPartitions.Where(x => x.Date < cutoff).OrderBy(x => x.Date).ToListAsync(cancellationToken);
        var dropped = 0;
        var blocked = 0;
        foreach (var partition in candidates)
        {
            await db.Entry(partition).ReloadAsync(cancellationToken);
            var start = new DateTimeOffset(partition.Date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
            var end = start.AddDays(1);
            var dirty = await db.DirtyMeasurementMinutes.AnyAsync(x => x.BucketStart >= start && x.BucketStart < end, cancellationToken);
            var raw = await db.MeasurementReadings.LongCountAsync(x => x.EffectiveDate == partition.Date, cancellationToken);
            var aggregated = await db.MeasurementMinutes.Where(x => x.BucketStart >= start && x.BucketStart < end).SumAsync(x => (long)x.SampleCount, cancellationToken);
            if (dirty || partition.Revision != partition.AggregatedRevision || raw != aggregated)
            {
                blocked++;
                continue;
            }
#pragma warning disable EF1002 // Table name contains only fixed prefix and invariant typed-date digits.
            await db.Database.ExecuteSqlRawAsync($"DROP TABLE \"{Table(partition.Date)}\"", cancellationToken);
#pragma warning restore EF1002
            db.MeasurementPartitions.Remove(partition);
            dropped++;
            db.AuditLogs.Add(new AuditLog
            {
                ActorUserId = "worker:retention",
                Action = "DropPartition",
                ResourceType = "MeasurementPartition",
                ResourceId = Guid.Empty,
                CorrelationId = "retention-" + Guid.NewGuid().ToString("N"),
                OccurredAt = clock.GetUtcNow(),
                After = JsonSerializer.Serialize(new { partition.Date, Samples = raw, partition.Revision, partition.AggregatedRevision })
            });
        }
        await db.SaveChangesAsync(cancellationToken);
        var minutes = await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"MeasurementMinutes\" m WHERE m.\"BucketStart\" < {minuteCutoff} AND NOT EXISTS (SELECT 1 FROM \"MeasurementPartitions\" p WHERE p.\"Date\" = (m.\"BucketStart\" AT TIME ZONE 'UTC')::date)", cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return new(dropped, blocked, minutes);
    }
}
