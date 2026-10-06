using PlantOps.Application.Assets;

namespace PlantOps.Application.Alarms;

public sealed record RuleInput(string Name, double RaiseAbove = 10, double ClearBelow = 8, int RaiseForMs = 3000,
    int ClearForMs = 5000, int MaxSampleGapMs = 2500, string Severity = "High", bool Enabled = true, long Version = 0);

public sealed record RuleDto(Guid Id, Guid PlantId, Guid PointId, string Name, string RuleType, double RaiseAbove,
    double ClearBelow, int RaiseForMs, int ClearForMs, int MaxSampleGapMs, string Severity, bool Enabled, long Version,
    bool HasActiveAlarm, DateTimeOffset? PendingRaiseSince, DateTimeOffset? PendingClearSince, DateTimeOffset? LastTrustedAt);

public sealed record AlarmDto(Guid Id, Guid PlantId, Guid AssetId, string AssetCode, string AssetName, Guid PointId,
    string PointCode, Guid RuleId, string RuleName, long RuleVersion, string Severity, string State,
    Guid TriggerReadingId, double TriggerValue, string ThresholdSnapshot, DateTimeOffset StartedAt,
    DateTimeOffset? RecoveredAt, DateTimeOffset? AcknowledgedAt, string? AcknowledgedByUserId,
    DateTimeOffset LastEvaluatedAt, long Version, bool CurrentObservationTrusted, bool TriggerReadingAvailable);

public sealed record AlarmPage(IReadOnlyList<AlarmDto> Items, string? NextCursor);

public sealed record AlarmEventDto(Guid Id, string EventType, DateTimeOffset OccurredAt, string? ActorUserId,
    Guid? ReadingId, string CorrelationId);

public sealed record AcknowledgeInput(long Version);

public interface IAlarmService
{
    Task<AlarmPage> ListAsync(Actor actor, Guid plantId, bool? active, Guid? assetId, int limit, string? cursor);

    Task<AlarmDto> GetAsync(Actor actor, Guid id);

    Task<IReadOnlyList<AlarmEventDto>> EventsAsync(Actor actor, Guid id);

    Task<AlarmDto> AcknowledgeAsync(Actor actor, Guid id, long version);

    Task<IReadOnlyList<RuleDto>> RulesAsync(Actor actor, Guid assetId, Guid pointId);

    Task<RuleDto> SaveRuleAsync(Actor actor, Guid assetId, Guid pointId, Guid? id, RuleInput input);
}

public interface IAlarmRuntimeReset
{
    Task ResetAsync(CancellationToken cancellationToken);
}
