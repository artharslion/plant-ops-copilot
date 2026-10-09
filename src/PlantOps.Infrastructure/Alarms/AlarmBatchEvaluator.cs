using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PlantOps.Application.Alarms;
using PlantOps.Application.Telemetry;
using PlantOps.Domain.Alarms;
using PlantOps.Domain.Identity;
using PlantOps.Domain.Telemetry;
using PlantOps.Infrastructure.Persistence;

namespace PlantOps.Infrastructure.Alarms;

// Caller owns the ingestion transaction and ordered plant locks. Nothing commits separately.
internal sealed class AlarmBatchEvaluator(PlantOpsDbContext db, TimeProvider clock)
{
    private List<AlarmRule> rules = [];
    private Dictionary<Guid, RuleRuntimeState> states = [];
    private Dictionary<Guid, Alarm> active = [];

    public async Task LoadAsync(Guid[] pointIds, CancellationToken ct)
    {
        rules = await db.AlarmRules.Where(x => x.Enabled && pointIds.Contains(x.PointId)).ToListAsync(ct);
        foreach (var rule in rules)
        {
            await db.Entry(rule).ReloadAsync(ct);
        }

        var ids = rules.Select(x => x.Id).ToArray();
        states = await db.RuleRuntimeStates.Where(x => ids.Contains(x.RuleId)).ToDictionaryAsync(x => x.RuleId, ct);
        active = await db.Alarms.Where(x => ids.Contains(x.RuleId) && x.State == "Active").ToDictionaryAsync(x => x.RuleId, ct);
        foreach (var state in states.Values)
        {
            await db.Entry(state).ReloadAsync(ct);
        }

        foreach (var alarm in active.Values)
        {
            await db.Entry(alarm).ReloadAsync(ct);
        }
    }

    public async Task ApplyAsync(MeasurementReading reading, SourceConfiguration config, string correlation, CancellationToken ct)
    {
        foreach (var rule in rules.Where(x => x.PointId == reading.PointId))
        {
            if (!states.TryGetValue(rule.Id, out var state))
            {
                state = new RuleRuntimeState { RuleId = rule.Id };
                states.Add(rule.Id, state);
                db.RuleRuntimeStates.Add(state);
            }
            active.TryGetValue(rule.Id, out var alarm);
            var transition = ThresholdEvaluator.Evaluate(rule, state, alarm != null, reading, config, clock.GetUtcNow());
            if (alarm != null && state.LastEvaluatedAt.HasValue)
            {
                alarm.LastEvaluatedAt = state.LastEvaluatedAt.Value;
            }

            if (transition == null)
            {
                continue;
            }

            string? before = null;
            if (transition == "Raised")
            {
                alarm = new Alarm
                {
                    PlantId = reading.PlantId,
                    AssetId = reading.AssetId,
                    PointId = reading.PointId,
                    RuleId = rule.Id,
                    RuleVersion = rule.Version,
                    Severity = rule.Severity,
                    TriggerReadingId = reading.Id,
                    TriggerValue = reading.NumericValue!.Value,
                    StartedAt = reading.EffectiveAt,
                    LastEvaluatedAt = reading.EffectiveAt,
                    ThresholdSnapshot = JsonSerializer.Serialize(new
                    {
                        rule.Name,
                        rule.RuleType,
                        rule.RaiseAbove,
                        rule.ClearBelow,
                        rule.RaiseForMs,
                        rule.ClearForMs,
                        rule.MaxSampleGapMs,
                        Unit = reading.CanonicalUnit
                    })
                };
                db.Alarms.Add(alarm);
                active.Add(rule.Id, alarm);
            }
            else
            {
                before = JsonSerializer.Serialize(alarm);
                alarm!.State = "Recovered";
                alarm.RecoveredAt = reading.EffectiveAt;
                alarm.Version++;
                active.Remove(rule.Id);
            }
            db.AlarmEvents.Add(new AlarmEvent
            {
                AlarmId = alarm!.Id,
                EventType = transition,
                ReadingId = reading.Id,
                OccurredAt = clock.GetUtcNow(),
                CorrelationId = correlation
            });
            db.AuditLogs.Add(new AuditLog
            {
                ActorUserId = "worker:alarm-engine",
                PlantId = reading.PlantId,
                ResourceId = alarm.Id,
                ResourceType = "Alarm",
                Action = transition,
                Before = before,
                After = JsonSerializer.Serialize(alarm),
                OccurredAt = clock.GetUtcNow(),
                CorrelationId = correlation
            });
            // Release the partial unique Active slot before a later sample can raise again.
            // This flush remains inside the caller's transaction and rolls back with the entire batch.
            if (transition == "Recovered")
            {
                await db.SaveChangesAsync(ct);
            }
        }
    }

    public Task ResetPointAsync(Guid pointId, DateTimeOffset inputAt, CancellationToken ct)
    {
        foreach (var rule in rules.Where(x => x.PointId == pointId))
        {
            // Older observations cannot invalidate a newer pending duration;
            // explicit loss at the current watermark still invalidates continuity.
            if (states.TryGetValue(rule.Id, out var state)
                && (!state.LastEvaluatedAt.HasValue || inputAt >= state.LastEvaluatedAt))
            {
                ThresholdEvaluator.ResetPending(state);
                state.Version++;
            }
        }

        return Task.CompletedTask;
    }
}

public sealed class AlarmRuntimeReset(PlantOpsDbContext db) : IAlarmRuntimeReset
{
    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        var plants = await (from state in db.RuleRuntimeStates
                            join rule in db.AlarmRules on state.RuleId equals rule.Id
                            select rule.PlantId).Distinct().OrderBy(x => x).ToListAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        foreach (var plant in plants)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({plant.ToString()}, 0))", cancellationToken);
        }

        var states = await db.RuleRuntimeStates.ToListAsync(cancellationToken);
        foreach (var state in states)
        {
            await db.Entry(state).ReloadAsync(cancellationToken);
            ThresholdEvaluator.ResetPending(state);
            state.Version++;
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
