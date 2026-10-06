using PlantOps.Application.Telemetry;
using PlantOps.Domain.Alarms;
using PlantOps.Domain.Telemetry;

namespace PlantOps.Application.Alarms;

public static class ThresholdEvaluator
{
    public static void ResetPending(RuleRuntimeState state)
    {
        state.PendingRaiseSince = null;
        state.PendingClearSince = null;
        state.LastInputTrusted = false;
    }

    // null means no business transition; persisted watermarks prevent replaying late observations.
    public static string? Evaluate(AlarmRule rule, RuleRuntimeState state, bool active,
        MeasurementReading reading, SourceConfiguration config, DateTimeOffset evaluatedAt)
    {
        if (reading.TimeAnomaly)
        {
            ResetPending(state);
            state.Version++;
            return null;
        }
        if (state.LastEvaluatedAt.HasValue && reading.EffectiveAt <= state.LastEvaluatedAt)
        {
            return null;
        }

        if (state.RuleVersion != rule.Version || state.MappingId != config.MappingId || state.MappingVersion != config.MappingVersion
            || state.SourceVersion != config.SourceVersion || state.PointVersion != config.PointVersion)
        {
            ResetPending(state);
        }

        state.RuleVersion = rule.Version;
        state.MappingId = config.MappingId;
        state.MappingVersion = config.MappingVersion;
        state.SourceVersion = config.SourceVersion;
        state.PointVersion = config.PointVersion;
        if (state.LastEvaluatedAt.HasValue && reading.EffectiveAt - state.LastEvaluatedAt > TimeSpan.FromMilliseconds(Math.Min(rule.MaxSampleGapMs, config.StaleAfterMs)))
        {
            ResetPending(state);
        }

        state.LastEvaluatedAt = reading.EffectiveAt;
        state.Version++;
        if (!rule.Enabled || !SamplePolicy.IsGood(reading) || !SamplePolicy.IsFresh(reading.EffectiveAt, reading.ReceivedAt, config.StaleAfterMs)
            || !SamplePolicy.IsFresh(reading.EffectiveAt, evaluatedAt, config.StaleAfterMs))
        {
            ResetPending(state);
            return null;
        }
        state.LastTrustedAt = reading.EffectiveAt;
        state.LastInputTrusted = true;
        if (!active)
        {
            state.PendingClearSince = null;
            if (reading.NumericValue <= rule.RaiseAbove)
            {
                state.PendingRaiseSince = null;
                return null;
            }
            state.PendingRaiseSince ??= reading.EffectiveAt;
            if (reading.EffectiveAt - state.PendingRaiseSince >= TimeSpan.FromMilliseconds(rule.RaiseForMs))
            {
                state.PendingRaiseSince = null;
                return "Raised";
            }
        }
        else
        {
            state.PendingRaiseSince = null;
            if (reading.NumericValue >= rule.ClearBelow)
            {
                state.PendingClearSince = null;
                return null;
            }
            state.PendingClearSince ??= reading.EffectiveAt;
            if (reading.EffectiveAt - state.PendingClearSince >= TimeSpan.FromMilliseconds(rule.ClearForMs))
            {
                state.PendingClearSince = null;
                return "Recovered";
            }
        }
        return null;
    }
}
