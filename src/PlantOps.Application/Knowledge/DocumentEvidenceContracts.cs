using PlantOps.Application.Assets;

namespace PlantOps.Application.Knowledge;

public sealed record DocumentEvidence(Guid DocumentId, Guid VersionId, int VersionNumber, Guid ChunkId,
    int Ordinal, string Title, string Section, int StartLine, int EndLine, string Text, string Sha256,
    string ParserVersion, int Score, string CitationPath);

public sealed record DocumentSearchResult(IReadOnlyList<DocumentEvidence> Items, IReadOnlyList<string> Warnings);

public interface IDocumentEvidenceService
{
    Task<DocumentSearchResult> SearchAsync(Actor actor, Guid assetId, string query, int limit = 6, CancellationToken cancellationToken = default);

    Task<DocumentEvidence> ExpandAsync(Actor actor, Guid assetId, Guid documentId, Guid versionId, Guid chunkId, CancellationToken cancellationToken = default);
}
