namespace PlantOps.Domain.Telemetry;

public sealed class MeasurementPoint
{
    public Guid Id { get; set; }
    public Guid PlantId { get; set; }
    public Guid AssetId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Quantity { get; set; } = "Vibration";
    public string CanonicalUnit { get; set; } = "mm/s";
    public string ValueType { get; set; } = "Double";
    public int ExpectedIntervalMs { get; set; } = 1000;
    public int StaleAfterMs { get; set; } = 5000;
    public bool Enabled { get; set; } = true;
    public long Version { get; set; } = 1;
}

public sealed class DataSource
{
    public Guid Id { get; set; }
    public Guid PlantId { get; set; }
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "Simulation";
    public string ConnectionParameters { get; set; } = "{\"schemaVersion\":1,\"intervalMs\":1000}";
    public bool Enabled { get; set; } = true;
    public long Version { get; set; } = 1;
}

public sealed class SourceMapping
{
    public Guid Id { get; set; }
    public Guid PlantId { get; set; }
    public Guid DataSourceId { get; set; }
    public Guid PointId { get; set; }
    public string SourceKey { get; set; } = "";
    public string RawUnit { get; set; } = "mm/s";
    public double Scale { get; set; } = 1;
    public double Offset { get; set; }
    public string SourceParameters { get; set; } = "{\"schemaVersion\":1,\"scenario\":\"GoldenPath\"}";
    public bool Enabled { get; set; } = true;
    public long Version { get; set; } = 1;
}

public sealed class MeasurementReading
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PlantId { get; set; }
    public Guid AssetId { get; set; }
    public Guid PointId { get; set; }
    public double? NumericValue { get; set; }
    public string CanonicalUnit { get; set; } = "";
    public string RawValueJson { get; set; } = "";
    public string RawUnit { get; set; } = "";
    public string SourceKind { get; set; } = "";
    public Guid DataSourceId { get; set; }
    public Guid SourceMappingId { get; set; }
    public long MappingVersion { get; set; }
    public uint? RawStatusCode { get; set; }
    public string? StatusSymbol { get; set; }
    public string Quality { get; set; } = "Bad";
    public DateTimeOffset? SourceTimestamp { get; set; }
    public DateTimeOffset? ServerTimestamp { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset EffectiveAt { get; set; }
    public string TimestampOrigin { get; set; } = "Receive";
    public string SourceEventKey { get; set; } = "";
    public bool TimeAnomaly { get; set; }
}

public sealed class PointLatestState
{
    public Guid PointId { get; set; }
    // Reading references deliberately have no FK: retained projections survive future partition cleanup.
    public Guid? LatestObservedReadingId { get; set; }
    public double? LatestObservedValue { get; set; }
    public string? LatestObservedQuality { get; set; }
    public DateTimeOffset? LatestObservedEffectiveAt { get; set; }
    public DateTimeOffset? LatestObservedReceivedAt { get; set; }
    public Guid? LastGoodReadingId { get; set; }
    public double? LastGoodValue { get; set; }
    public DateTimeOffset? LastTrustedEffectiveAt { get; set; }
    public DateTimeOffset? LastGoodReceivedAt { get; set; }
    public DateTimeOffset LastReceivedAt { get; set; }
    public long Version { get; set; } = 1;
}
