using PlantOps.Application.Assets;

namespace PlantOps.Application.Telemetry;

public sealed record SourceConfiguration(Guid PlantId, Guid AssetId, Guid PointId, long PointVersion,
    string CanonicalUnit, int ExpectedIntervalMs, int StaleAfterMs, Guid DataSourceId, long SourceVersion,
    string SourceKind, Guid MappingId, long MappingVersion, string SourceKey, string RawUnit,
    double Scale, double Offset, string SourceParameters);
public sealed record NormalizedSample(SourceConfiguration Configuration, double? NumericValue, string Quality,
    string RawValueJson, uint? RawStatusCode, string? StatusSymbol, DateTimeOffset? SourceTimestamp,
    DateTimeOffset? ServerTimestamp, DateTimeOffset ReceivedAt, string SourceEventKey);
public interface IMeasurementSource
{
    string Kind { get; }
    IAsyncEnumerable<NormalizedSample> ReadAsync(IReadOnlyList<SourceConfiguration> configurations, CancellationToken cancellationToken);
}
public sealed record IngestResult(int Inserted, int Duplicates);
public interface IMeasurementIngestor
{
    Task<IngestResult> IngestAsync(IReadOnlyList<NormalizedSample> samples, CancellationToken cancellationToken = default);
}
public interface ISourceConfigurationReader
{
    Task<IReadOnlyList<SourceConfiguration>> GetEnabledAsync(CancellationToken cancellationToken = default);
}
public sealed record ReadingSnapshot(Guid Id, double? Value, string Quality, DateTimeOffset EffectiveAt,
    DateTimeOffset ReceivedAt, bool IsFresh, bool IsTrusted);
public sealed record PointOverview(Guid Id, Guid AssetId, string Code, string Name, string Quantity,
    string CanonicalUnit, string ValueType, int ExpectedIntervalMs, int StaleAfterMs, bool Enabled, long Version,
    ReadingSnapshot? LatestObserved, ReadingSnapshot? LastGood, DateTimeOffset? LastReceivedAt,
    Guid? DataSourceId, Guid? MappingId, string? SourceKind, string? SourceKey, long? MappingVersion);
public sealed record TrendSample(Guid Id, DateTimeOffset EffectiveAt, DateTimeOffset ReceivedAt,
    double? Value, string Quality, bool TimeAnomaly, bool GapBefore, Guid DataSourceId, Guid SourceMappingId,
    long MappingVersion, string SourceKind, string TimestampOrigin, DateTimeOffset? SourceTimestamp,
    DateTimeOffset? ServerTimestamp, uint? RawStatusCode, string? StatusSymbol);
public sealed record TrendBucket(DateTimeOffset Start, double? Min, double? Max, double? Average,
    int GoodCount, int UncertainCount, int BadCount, int AnomalyCount, bool HasGap, double CoverageRatio);
public sealed record MeasurementTrend(Guid PointId, string Unit, DateTimeOffset From, DateTimeOffset To,
    string Bucket, DateTimeOffset CheckedAt, IReadOnlyList<TrendSample> Samples,
    IReadOnlyList<TrendBucket> Buckets, bool LeadingGap, bool TrailingGap);
public sealed record PointInput(string Code, string Name, string Quantity = "Vibration", string CanonicalUnit = "mm/s",
    int ExpectedIntervalMs = 1000, int StaleAfterMs = 5000, bool Enabled = true, long Version = 0);
public sealed record SourceInput(string Name, bool Enabled = true, long Version = 0);
public sealed record SourceDto(Guid Id, Guid PlantId, string Name, string Kind, bool Enabled, long Version);
public sealed record MappingInput(Guid DataSourceId, string SourceKey, string RawUnit = "mm/s", double Scale = 1,
    double Offset = 0, bool Enabled = true, long Version = 0);
public sealed record MappingDto(Guid Id, Guid PlantId, Guid PointId, Guid DataSourceId, string SourceKey,
    string RawUnit, double Scale, double Offset, string SourceParameters, bool Enabled, long Version);
public interface ITelemetryService
{
    Task<IReadOnlyList<PointOverview>> PointsAsync(Actor actor, Guid assetId);
    Task<PointOverview> SavePointAsync(Actor actor, Guid assetId, Guid? pointId, PointInput input);
    Task<MeasurementTrend> TrendAsync(Actor actor, Guid assetId, Guid pointId, DateTimeOffset from, DateTimeOffset to, string bucket);
    Task<IReadOnlyList<SourceDto>> SourcesAsync(Actor actor, Guid plantId);
    Task<SourceDto> SaveSourceAsync(Actor actor, Guid plantId, Guid? id, SourceInput input);
    Task<IReadOnlyList<MappingDto>> MappingsAsync(Actor actor, Guid assetId, Guid pointId);
    Task<MappingDto> SaveMappingAsync(Actor actor, Guid assetId, Guid pointId, Guid? id, MappingInput input);
}
