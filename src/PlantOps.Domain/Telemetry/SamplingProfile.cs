namespace PlantOps.Domain.Telemetry;

public sealed class SamplingProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid PointId
    {
        get; set;
    }

    public Guid SourceMappingId
    {
        get; set;
    }

    public long MappingVersion
    {
        get; set;
    }

    public DateTimeOffset EffectiveFrom
    {
        get; set;
    }

    public DateTimeOffset? EffectiveTo
    {
        get; set;
    }

    public string Mode { get; set; } = "Unknown";

    public double? RevisedSamplingIntervalMs
    {
        get; set;
    }

    public double? RevisedPublishingIntervalMs
    {
        get; set;
    }

    public long? RevisedQueueSize
    {
        get; set;
    }
}
