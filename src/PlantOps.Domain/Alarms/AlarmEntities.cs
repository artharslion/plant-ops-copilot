namespace PlantOps.Domain.Alarms;

public sealed class AlarmRule
{
    public Guid Id { get; set; }
    public Guid PlantId { get; set; }
    public Guid PointId { get; set; }
    public string Name { get; set; } = "";
    public string RuleType { get; set; } = "HighThreshold";
    public double RaiseAbove { get; set; } = 10;
    public double ClearBelow { get; set; } = 8;
    public int RaiseForMs { get; set; } = 3000;
    public int ClearForMs { get; set; } = 5000;
    public int MaxSampleGapMs { get; set; } = 2500;
    public string Severity { get; set; } = "High";
    public bool Enabled { get; set; } = true;
    public long Version { get; set; } = 1;
}

public sealed class RuleRuntimeState
{
    public Guid RuleId { get; set; }
    public DateTimeOffset? PendingRaiseSince { get; set; }
    public DateTimeOffset? PendingClearSince { get; set; }
    public DateTimeOffset? LastEvaluatedAt { get; set; }
    public DateTimeOffset? LastTrustedAt { get; set; }
    public bool LastInputTrusted { get; set; }
    public Guid? MappingId { get; set; }
    public long MappingVersion { get; set; }
    public long SourceVersion { get; set; }
    public long PointVersion { get; set; }
    public long RuleVersion { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class Alarm
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PlantId { get; set; }
    public Guid AssetId { get; set; }
    public Guid PointId { get; set; }
    public Guid RuleId { get; set; }
    public long RuleVersion { get; set; }
    public string Severity { get; set; } = "High";
    public string State { get; set; } = "Active";
    public Guid TriggerReadingId { get; set; }
    public double TriggerValue { get; set; }
    public string ThresholdSnapshot { get; set; } = "{}";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? RecoveredAt { get; set; }
    public DateTimeOffset? AcknowledgedAt { get; set; }
    public string? AcknowledgedByUserId { get; set; }
    public DateTimeOffset LastEvaluatedAt { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class AlarmEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AlarmId { get; set; }
    public string EventType { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }
    public string? ActorUserId { get; set; }
    public Guid? ReadingId { get; set; }
    public string CorrelationId { get; set; } = "";
}
