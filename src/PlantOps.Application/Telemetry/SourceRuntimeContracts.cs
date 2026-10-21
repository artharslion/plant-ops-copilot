namespace PlantOps.Application.Telemetry;

public sealed record SourceHealth(Guid DataSourceId, long SourceVersion, string State,
    DateTimeOffset UpdatedAt, DateTimeOffset? LastKeepAliveAt, DateTimeOffset? LastReceivedAt,
    int ReconnectCount, string? FailureCode, bool IsCurrent = true);

public interface ISourceRuntimeStore
{
    Task ActivateAsync(IReadOnlyList<SourceConfiguration> configurations, IReadOnlyList<SamplingRevision> revisions, CancellationToken ct);

    Task CloseAsync(IReadOnlyList<SamplingRevision> revisions, DateTimeOffset at, CancellationToken ct);

    Task ReportAsync(SourceHealth health, CancellationToken ct);

    Task RecordLossAsync(IReadOnlyList<SourceConfiguration> configurations, DateTimeOffset at, string reason, CancellationToken ct);
}

public sealed class SourceConfigurationException(string code) : Exception(code);

public interface IOpcUaConnection : IAsyncDisposable
{
    IReadOnlyList<SamplingRevision> Revisions
    {
        get;
    }

    DateTimeOffset? LastKeepAliveAt
    {
        get;
    }

    IAsyncEnumerable<NormalizedSample> ReadAsync(CancellationToken ct);
}

public interface IOpcUaConnector
{
    Task<IOpcUaConnection> ConnectAsync(IReadOnlyList<SourceConfiguration> configurations, CancellationToken ct);
}
