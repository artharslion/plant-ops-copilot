using PlantOps.Application.Assets;

namespace PlantOps.Application.Knowledge;

public sealed record DocumentInput(Guid PlantId, Guid? AssetId, string Title, string Category = "Maintenance", string Visibility = "PlantReaders", string[]? AllowedRoles = null, long Version = 0, bool IsArchived = false);

public sealed record DocumentDto(Guid Id, Guid PlantId, Guid? AssetId, string Title, string Category, string Visibility, string[] AllowedRoles, Guid? CurrentPublishedVersionId, bool IsArchived, long Version);

public sealed record DocumentVersionDto(Guid Id, int VersionNumber, string FileName, string MediaType, string Sha256, long SizeBytes, string Status, string UploadedByUserId, DateTimeOffset UploadedAt, DateTimeOffset? PublishedAt, string IndexStatus);

public sealed record DocumentPage(IReadOnlyList<DocumentDto> Items, string? NextCursor);

public sealed record PublishDocument(long Version, int VersionNumber);

public sealed record DocumentFile(string FileName, string MediaType, byte[] Content);

public interface IDocumentService
{
    Task<DocumentPage> ListAsync(Actor actor, Guid plantId, Guid? assetId, int limit, string? cursor);

    Task<DocumentDto> GetAsync(Actor actor, Guid id);

    Task<IReadOnlyList<DocumentVersionDto>> VersionsAsync(Actor actor, Guid id);

    Task<DocumentDto> UploadAsync(Actor actor, Guid? id, DocumentInput input, DocumentFile file);

    Task<DocumentDto> ConfigureAsync(Actor actor, Guid id, DocumentInput input);

    Task<DocumentDto> PublishAsync(Actor actor, Guid id, PublishDocument input, bool withdraw);

    Task<DocumentFile> DownloadAsync(Actor actor, Guid id, int version);
}

public interface IDocumentFileStore
{
    Task<string> PutAsync(byte[] content);

    Task<byte[]> ReadAsync(string key);

    Task<byte[]> ReadBoundedAsync(string key, int maximumBytes, CancellationToken cancellationToken = default);

    Task RemoveAsync(string key);
}
