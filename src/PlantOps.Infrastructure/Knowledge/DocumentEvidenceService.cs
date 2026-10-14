using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PlantOps.Application.Assets;
using PlantOps.Application.Knowledge;
using PlantOps.Domain.Knowledge;

namespace PlantOps.Infrastructure.Knowledge;

public sealed partial class DocumentService
{
    private void SaveParsedChunks(DocumentVersion version, ParsedDocument parsed)
    {
        version.IndexStatus = parsed.Status;
        version.ParserVersion = TextDocumentParser.Version;
        foreach (var chunk in parsed.Chunks)
        {
            db.DocumentChunks.Add(new DocumentChunk
            {
                DocumentId = version.DocumentId,
                VersionId = version.Id,
                Ordinal = chunk.Ordinal,
                Section = chunk.Section,
                StartLine = chunk.StartLine,
                EndLine = chunk.EndLine,
                Text = chunk.Text,
                Sha256 = chunk.Sha256,
                ParserVersion = TextDocumentParser.Version
            });
        }
    }

    private async Task<string[]> EvidenceRolesAsync(Actor actor, Guid assetId, Guid plantId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var roles = (await Roles(actor, cancellationToken: cancellationToken)).ToArray();
        if (!roles.Intersect(new[] { "Viewer", "Operator", "MaintenanceEngineer", "Admin" }).Any())
        {
            throw new DirectoryException(403, "Role does not allow this action.");
        }
        // Query the source again even in a long-lived scoped Worker with tracked entities.
        if (!await db.UserPlantAccess.AsNoTracking().AnyAsync(x => x.UserId == actor.UserId && x.PlantId == plantId, cancellationToken)
            || !await db.Assets.AsNoTracking().AnyAsync(x => x.Id == assetId && x.PlantId == plantId && !x.IsArchived, cancellationToken))
        {
            throw new DirectoryException(404, "Resource not found.");
        }
        return roles;
    }

    private IQueryable<Document> EvidenceDocuments(Guid plantId, Guid assetId, string[] roles) =>
        db.Documents.AsNoTracking().Where(x => x.PlantId == plantId && !x.IsArchived && x.CurrentPublishedVersionId != null
            && (x.AssetId == null || x.AssetId == assetId)
            && (x.Visibility == "PlantReaders" || x.AllowedRoles.Any(r => roles.Contains(r))));

    private async Task IndexLegacyAsync(DocumentVersion version, CancellationToken cancellationToken)
    {
        if (version.ParserVersion == TextDocumentParser.Version)
        {
            return;
        }
        // A parser upgrade needs its own migration/reindex contract; never mutate an existing ledger's chunks.
        if (version.ParserVersion != null)
        {
            return;
        }
        ParsedDocument parsed;
        if (version.SizeBytes > TextDocumentParser.MaximumBytes)
        {
            parsed = new(version.MediaType == "application/pdf" ? "NeedsText" : "TooLarge", []);
        }
        else
        {
            try
            {
                var bytes = await files.ReadBoundedAsync(version.StorageKey, checked((int)version.SizeBytes), cancellationToken);
                parsed = bytes.LongLength != version.SizeBytes || Convert.ToHexStringLower(SHA256.HashData(bytes)) != version.Sha256
                    ? new("SourceMismatch", []) : TextDocumentParser.Parse(version.FileName, version.MediaType, bytes);
            }
            catch (IOException) { parsed = new("Unavailable", []); }
            catch (UnauthorizedAccessException) { parsed = new("Unavailable", []); }
        }
        // Attach a fresh snapshot only after source authorization; older tracked version state is discarded.
        var tracked = db.DocumentVersions.Local.SingleOrDefault(x => x.Id == version.Id);
        if (tracked != null)
        {
            db.Entry(tracked).State = EntityState.Detached;
        }
        db.Attach(version);
        SaveParsedChunks(version, parsed);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static DocumentEvidence Evidence(Guid assetId, Document document, DocumentVersion version, DocumentChunk chunk, int score) =>
        new(document.Id, version.Id, version.VersionNumber, chunk.Id, chunk.Ordinal, document.Title, chunk.Section, chunk.StartLine, chunk.EndLine,
            chunk.Text, chunk.Sha256, chunk.ParserVersion, score,
            $"/api/v1/assets/{assetId}/document-evidence/{document.Id}/{version.Id}/{chunk.Id}");

    public async Task<DocumentSearchResult> SearchAsync(Actor actor, Guid assetId, string query, int limit = 6, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(query) || query.Length > 200 || limit is < 1 or > 6)
        {
            throw new DirectoryException(400, "Use a query up to 200 characters and a result limit of 1 to 6.");
        }
        var terms = Regex.Matches(query, @"[\p{L}\p{N}][\p{L}\p{N}_\-]*", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))
            .Select(x => x.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (terms.Length is < 1 or > 12)
        {
            throw new DirectoryException(400, "Use 1 to 12 search terms.");
        }
        var asset = await directory.GetAsync(actor, "assets", assetId);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({asset.PlantId.ToString()}, 0))", cancellationToken);
        var roles = await EvidenceRolesAsync(actor, assetId, asset.PlantId, cancellationToken);
        var candidates = await (from document in EvidenceDocuments(asset.PlantId, assetId, roles)
                                join version in db.DocumentVersions.AsNoTracking() on document.CurrentPublishedVersionId equals version.Id
                                where version.DocumentId == document.Id && version.Status == "Published"
                                orderby document.Id
                                select new
                                {
                                    Document = document,
                                    Version = version
                                }).Take(51).ToListAsync(cancellationToken);
        var warnings = new HashSet<string>(StringComparer.Ordinal);
        if (candidates.Count > 50)
        {
            warnings.Add("已授权资料超过本次检索上限，本次只检查其中 50 份，结果可能不完整。");
        }
        var matches = new List<DocumentEvidence>();
        long legacyBytes = 0;
        foreach (var candidate in candidates.Take(50))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var version = candidate.Version;
            if (version.ParserVersion == null)
            {
                // A single query never opens more than 1 MiB of legacy source files.
                if (version.SizeBytes <= TextDocumentParser.MaximumBytes && legacyBytes + version.SizeBytes > 1024 * 1024)
                {
                    warnings.Add("部分旧资料尚未生成片段，本次结果可能不完整。请稍后重新查询。");
                    continue;
                }
                legacyBytes += version.SizeBytes <= TextDocumentParser.MaximumBytes ? version.SizeBytes : 0;
                await IndexLegacyAsync(version, cancellationToken);
            }
            if (version.IndexStatus != "Ready" || version.ParserVersion != TextDocumentParser.Version)
            {
                warnings.Add(version.IndexStatus == "NeedsText" ? "部分已授权资料为未解析 PDF，未纳入文本检索。"
                    : "部分已授权资料为空、超出解析上限或暂不可解析，未纳入文本检索。");
                continue;
            }
            var chunks = await db.DocumentChunks.AsNoTracking().Where(x => x.VersionId == version.Id && x.DocumentId == candidate.Document.Id
                && x.ParserVersion == TextDocumentParser.Version).OrderBy(x => x.Ordinal).Take(TextDocumentParser.MaximumChunks).ToListAsync(cancellationToken);
            foreach (var chunk in chunks)
            {
                var score = terms.Count(t => chunk.Text.Contains(t, StringComparison.OrdinalIgnoreCase))
                    + terms.Count(t => chunk.Section.Contains(t, StringComparison.OrdinalIgnoreCase))
                    + terms.Count(t => candidate.Document.Title.Contains(t, StringComparison.OrdinalIgnoreCase));
                if (score > 0)
                {
                    matches.Add(Evidence(assetId, candidate.Document, version, chunk, score));
                }
            }
        }
        var currentRoles = await EvidenceRolesAsync(actor, assetId, asset.PlantId, cancellationToken);
        if (!roles.ToHashSet(StringComparer.Ordinal).SetEquals(currentRoles))
        {
            throw new DirectoryException(404, "Authorization changed. Search again.");
        }
        await tx.CommitAsync(cancellationToken);
        return new(matches.OrderByDescending(x => x.Score).ThenBy(x => x.DocumentId).ThenBy(x => x.Ordinal).Take(limit).ToArray(), warnings.Order().ToArray());
    }

    public async Task<DocumentEvidence> ExpandAsync(Actor actor, Guid assetId, Guid documentId, Guid versionId, Guid chunkId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var asset = await directory.GetAsync(actor, "assets", assetId);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({asset.PlantId.ToString()}, 0))", cancellationToken);
        var roles = await EvidenceRolesAsync(actor, assetId, asset.PlantId, cancellationToken);
        var document = await EvidenceDocuments(asset.PlantId, assetId, roles).SingleOrDefaultAsync(x => x.Id == documentId && x.CurrentPublishedVersionId == versionId, cancellationToken)
            ?? throw new DirectoryException(404, "Evidence not available.");
        var version = await db.DocumentVersions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == versionId && x.DocumentId == documentId
            && x.Status == "Published" && x.IndexStatus == "Ready" && x.ParserVersion == TextDocumentParser.Version, cancellationToken)
            ?? throw new DirectoryException(404, "Evidence not available.");
        var chunk = await db.DocumentChunks.AsNoTracking().SingleOrDefaultAsync(x => x.Id == chunkId && x.DocumentId == documentId
            && x.VersionId == versionId && x.ParserVersion == TextDocumentParser.Version, cancellationToken)
            ?? throw new DirectoryException(404, "Evidence not available.");
        var currentRoles = await EvidenceRolesAsync(actor, assetId, asset.PlantId, cancellationToken);
        if (!Allowed(document, currentRoles))
        {
            throw new DirectoryException(404, "Evidence not available.");
        }
        var result = Evidence(assetId, document, version, chunk, 0);
        await tx.CommitAsync(cancellationToken);
        return result;
    }
}
