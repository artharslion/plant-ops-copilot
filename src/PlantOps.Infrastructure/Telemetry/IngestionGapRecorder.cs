using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
using PlantOps.Application.Alarms;
using PlantOps.Application.Telemetry;
using PlantOps.Domain.Telemetry;
using PlantOps.Infrastructure.Persistence;

namespace PlantOps.Infrastructure.Telemetry;

public sealed class IngestionGapRecorder(PlantOpsDbContext db, TimeProvider clock) : IIngestionGapRecorder
{
    public async Task DetectStartupAsync(IReadOnlyList<SourceConfiguration> configurations, CancellationToken cancellationToken = default)
    {
        var ids = configurations.Select(x => x.PointId).ToArray();
        var states = await db.PointLatestStates.AsNoTracking().Where(x => ids.Contains(x.PointId)).ToDictionaryAsync(x => x.PointId, cancellationToken);
        var now = clock.GetUtcNow();
        var losses = new List<IngestionLoss>();
        foreach (var c in configurations)
        {
            if (!states.TryGetValue(c.PointId, out var state) || state.LatestObservedEffectiveAt is not DateTimeOffset prior
                || now - prior <= TimeSpan.FromMilliseconds(c.ExpectedIntervalMs * 1.5))
            {
                continue;
            }

            var from = prior.AddMilliseconds(c.ExpectedIntervalMs);
            // The committed watermark gives a stable restart episode, not a claim of exact lost sample count.
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{c.PointId:N}:{c.DataSourceId:N}:{from.UtcTicks}:PossibleCrashTail"));
            losses.Add(new(new Guid(hash.AsSpan(0, 16)), c, from, null, "PossibleCrashTail", null, true));
        }
        foreach (var batch in losses.Chunk(1000))
        {
            await RecordAsync(batch, cancellationToken);
        }
    }

    public async Task RecordAsync(IReadOnlyList<IngestionLoss> losses, CancellationToken cancellationToken = default)
    {
        if (losses.Count == 0)
        {
            return;
        }

        if (losses.Count > 1000 || losses.Any(x => x.From.Offset != TimeSpan.Zero || x.To?.Offset != TimeSpan.Zero && x.To.HasValue
            || x.To < x.From || x.DroppedCount < 0 || x.Reason.Length is < 1 or > 64))
        {
            throw new ArgumentException("Invalid ingestion loss summary.");
        }

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        await MeasurementMaintenance.LockAsync(db, cancellationToken);
        foreach (var plant in losses.Select(x => x.Configuration.PlantId).Distinct().Order())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({plant.ToString()}, 0))", cancellationToken);
        }

        foreach (var loss in losses)
        {
            var c = loss.Configuration;
            // Disabled mappings still need a loss record; verify ownership rather than enabled/version state.
            if (!await db.SourceMappings.AnyAsync(x => x.Id == c.MappingId && x.PointId == c.PointId && x.PlantId == c.PlantId, cancellationToken)
                || !await db.DataSources.AnyAsync(x => x.Id == c.DataSourceId && x.PlantId == c.PlantId, cancellationToken))
            {
                throw new InvalidOperationException("Loss summary is outside its source/plant.");
            }

            var gap = await db.IngestionGaps.FindAsync([loss.Id], cancellationToken);
            var created = gap == null;
            if (gap == null)
            {
                gap = new IngestionGap
                {
                    Id = loss.Id,
                    PointId = c.PointId,
                    DataSourceId = c.DataSourceId,
                    From = loss.From,
                    To = loss.To,
                    Reason = loss.Reason,
                    DetectedAt = clock.GetUtcNow()
                };
                db.IngestionGaps.Add(gap);
            }
            else
            {
                await db.Entry(gap).ReloadAsync(cancellationToken);
                if (gap.PointId != c.PointId || gap.DataSourceId != c.DataSourceId || gap.Reason != loss.Reason)
                {
                    throw new InvalidOperationException("Loss ID belongs to another episode.");
                }

                if (loss.From < gap.From)
                {
                    gap.From = loss.From;
                }
                // A replay of an open summary must not reopen an episode already recovered.
                if (loss.To.HasValue && (!gap.To.HasValue || loss.To > gap.To))
                {
                    gap.To = loss.To;
                }
            }
            if (gap.To.HasValue)
            {
                gap.RecoveredAt ??= clock.GetUtcNow();
            }

            gap.DroppedCount = gap.DroppedCount.HasValue && loss.DroppedCount.HasValue ? Math.Max(gap.DroppedCount.Value, loss.DroppedCount.Value) : loss.DroppedCount;
            gap.IsPossible |= loss.IsPossible;
            var states = await (from state in db.RuleRuntimeStates
                                join rule in db.AlarmRules on state.RuleId equals rule.Id
                                where rule.PointId == c.PointId
                                select state).ToListAsync(cancellationToken);
            foreach (var state in states)
            {
                await db.Entry(state).ReloadAsync(cancellationToken);
                var pendingAt = state.PendingRaiseSince ?? state.PendingClearSince;
                if (created && (!gap.To.HasValue || pendingAt.HasValue && gap.To > pendingAt))
                {
                    ThresholdEvaluator.ResetPending(state);
                    state.Version++;
                }
            }
        }
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    public async Task RecoverAsync(IReadOnlyList<SourceConfiguration> configurations, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        if (configurations.Count == 0)
        {
            return;
        }

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        await MeasurementMaintenance.LockAsync(db, cancellationToken);
        // Join ownership; an internal caller cannot close another factory's open episode by point ID alone.
        foreach (var c in configurations.Distinct())
        {
            await db.IngestionGaps.Where(x => x.PointId == c.PointId && x.DataSourceId == c.DataSourceId && x.To == null && x.From <= at
                && db.MeasurementPoints.Any(p => p.Id == c.PointId && p.PlantId == c.PlantId))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.To, at).SetProperty(x => x.RecoveredAt, at), cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
    }
}
