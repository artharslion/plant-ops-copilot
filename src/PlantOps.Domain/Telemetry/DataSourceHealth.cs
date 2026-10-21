namespace PlantOps.Domain.Telemetry;

public sealed class DataSourceHealth
{
    public Guid DataSourceId
    {
        get; set;
    }

    public long SourceVersion
    {
        get; set;
    }

    public string State { get; set; } = "Stopped";

    public DateTimeOffset UpdatedAt
    {
        get; set;
    }

    public DateTimeOffset? LastKeepAliveAt
    {
        get; set;
    }

    public DateTimeOffset? LastReceivedAt
    {
        get; set;
    }

    public int ReconnectCount
    {
        get; set;
    }

    public string? FailureCode
    {
        get; set;
    }
}
