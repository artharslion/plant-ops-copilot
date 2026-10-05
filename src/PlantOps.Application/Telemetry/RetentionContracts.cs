namespace PlantOps.Application.Telemetry;
public sealed record RetentionResult(int DroppedPartitions, int BlockedPartitions, int DeletedMinutes);
public interface IMeasurementMaintenance
{
    Task<int> AggregateAsync(CancellationToken cancellationToken = default);
    Task<RetentionResult> RetainAsync(CancellationToken cancellationToken = default);
}
