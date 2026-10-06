using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using PlantOps.Application.Assets;
using PlantOps.Application.Knowledge;
using PlantOps.Domain.Identity;
using PlantOps.Domain.Knowledge;
using PlantOps.Infrastructure.Identity;
using PlantOps.Infrastructure.Persistence;

namespace PlantOps.Infrastructure.Knowledge;

public sealed class DocumentService(PlantOpsDbContext db, IAssetDirectory directory, UserManager<PlantOpsUser> users, IDocumentFileStore files, TimeProvider clock) : IDocumentService
{
    private async Task<IList<string>> Roles(Actor actor, bool write = false)
    {
        var user = await users.FindByIdAsync(actor.UserId);
        if (user is not { IsEnabled: true })
        {
            throw new DirectoryException(401, "Session unavailable.");
        }

        var r = await users.GetRolesAsync(user);
        if (write && !r.Intersect(new[] { "Admin", "MaintenanceEngineer" }).Any())
        {
            throw new DirectoryException(403, "Maintenance role required.");
        }

        return r;
    }

    private static bool Allowed(Document d, IList<string> roles) => !d.IsArchived && (d.Visibility == "PlantReaders" || d.AllowedRoles.Intersect(roles).Any());

    private async Task<Document> Load(Actor actor, Guid id, bool write = false)
    {
        var r = await Roles(actor, write);
        var d = await db.Documents.SingleOrDefaultAsync(x => x.Id == id) ?? throw new DirectoryException(404, "Document not found.");
        await directory.GetAsync(actor, "plants", d.PlantId);
        if (!Allowed(d, r) || !d.CurrentPublishedVersionId.HasValue && !r.Intersect(new[] { "Admin", "MaintenanceEngineer" }).Any())
        {
            throw new DirectoryException(404, "Document not found.");
        }

        return d;
    }

    private static DocumentDto Dto(Document d) => new(d.Id, d.PlantId, d.AssetId, d.Title, d.Category, d.Visibility, d.AllowedRoles, d.CurrentPublishedVersionId, d.IsArchived, d.Version);

    public async Task<DocumentDto> GetAsync(Actor actor, Guid id) => Dto(await Load(actor, id));

    public async Task<DocumentPage> ListAsync(Actor actor, Guid plantId, Guid? assetId, int limit, string? cursor)
    {
        await directory.GetAsync(actor, "plants", plantId);
        var roles = (await Roles(actor)).ToArray();
        var maintenance = roles.Intersect(new[] { "Admin", "MaintenanceEngineer" }).Any();
        if (assetId.HasValue && (await directory.GetAsync(actor, "assets", assetId.Value)).PlantId != plantId)
        {
            throw new DirectoryException(404, "Asset not found.");
        }

        if (limit is < 1 or > 200)
        {
            throw new DirectoryException(400, "Invalid page size.");
        }

        var q = db.Documents.AsNoTracking().Where(x => x.PlantId == plantId && !x.IsArchived && (!assetId.HasValue || x.AssetId == assetId || x.AssetId == null)
            && (maintenance || x.CurrentPublishedVersionId != null) && (x.Visibility == "PlantReaders" || x.AllowedRoles.Any(r => roles.Contains(r))));
        if (cursor != null)
        {
            if (!Guid.TryParseExact(cursor, "N", out var id))
            {
                throw new DirectoryException(400, "Invalid cursor.");
            }

            q = q.Where(x => x.Id.CompareTo(id) > 0);
        }
        var rows = await q.OrderBy(x => x.Id).Take(limit + 1).ToListAsync();
        var page = rows.Take(limit).ToList();
        return new(page.Select(Dto).ToList(), rows.Count > limit ? page[^1].Id.ToString("N") : null);
    }

    public async Task<IReadOnlyList<DocumentVersionDto>> VersionsAsync(Actor actor, Guid id)
    {
        await Load(actor, id);
        var maintenance = (await Roles(actor)).Intersect(new[] { "Admin", "MaintenanceEngineer" }).Any();
        return await db.DocumentVersions.AsNoTracking().Where(x => x.DocumentId == id && (maintenance || x.Status == "Published")).OrderByDescending(x => x.VersionNumber)
            .Select(x => new DocumentVersionDto(x.Id, x.VersionNumber, x.FileName, x.MediaType, x.Sha256, x.SizeBytes, x.Status, x.UploadedByUserId, x.UploadedAt, x.PublishedAt, x.IndexStatus)).ToListAsync();
    }

    private static void Validate(DocumentInput i)
    {
        if (string.IsNullOrWhiteSpace(i.Title) || i.Title.Length > 200 || string.IsNullOrWhiteSpace(i.Category) || i.Category.Length > 64
            || i.Visibility is not ("PlantReaders" or "RestrictedRoles") || i.AllowedRoles?.Length > 4
            || i.AllowedRoles?.Any(x => x is not ("Viewer" or "Operator" or "MaintenanceEngineer" or "Admin")) == true
            || i.Visibility == "RestrictedRoles" && !(i.AllowedRoles?.Length > 0))
        {
            throw new DirectoryException(400, "Invalid document metadata or ACL.");
        }
    }

    private static string ValidateFile(DocumentFile file)
    {
        if (file.Content.Length is < 1 or > 10485760 || string.IsNullOrWhiteSpace(file.FileName) || file.FileName.Length > 200
            || file.FileName.Contains('/') || file.FileName.Contains('\\') || file.FileName.Any(char.IsControl))
        {
            throw new DirectoryException(400, "Invalid file name or size (maximum 10 MiB).");
        }

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext == ".pdf" && file.MediaType == "application/pdf")
        {
            if (!file.Content.AsSpan().StartsWith("%PDF-"u8) || !Encoding.ASCII.GetString(file.Content.AsSpan(Math.Max(0, file.Content.Length - 1024))).Contains("%%EOF", StringComparison.Ordinal))
            {
                throw new DirectoryException(400, "Invalid PDF header or footer.");
            }

            return "NeedsText";
        }
        if (!(ext == ".md" && file.MediaType is "text/markdown" or "text/plain" || ext == ".txt" && file.MediaType == "text/plain"))
        {
            throw new DirectoryException(400, "Only PDF, Markdown and UTF-8 TXT with matching media types are allowed.");
        }

        try
        {
            var text = new UTF8Encoding(false, true).GetString(file.Content);
            if (text.Contains('\0') || text.TrimStart('\uFEFF', ' ', '\r', '\n', '\t').StartsWith("<", StringComparison.Ordinal))
            {
                throw new DecoderFallbackException();
            }
        }
        catch (DecoderFallbackException) { throw new DirectoryException(400, "Text files must contain UTF-8 text, without binary or HTML headers."); }
        return "NotRequested";
    }

    private async Task Lock(Guid plant) => await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({plant.ToString()}, 0))");

    private static void Version(Document d, long v)
    {
        if (d.Version != v)
        {
            throw new DirectoryException(409, "Document version conflict.");
        }
    }

    private void Audit(Actor actor, Document d, string action, string? before, object? extra = null)
    {
        db.AuditLogs.Add(new AuditLog { PlantId = d.PlantId, ResourceId = d.Id, ResourceType = "Document", Action = action, ActorUserId = actor.UserId, Before = before, After = JsonSerializer.Serialize(new { document = Dto(d), version = extra }), CorrelationId = actor.CorrelationId, OccurredAt = clock.GetUtcNow() });
    }

    private async Task Save()
    {
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException) { throw new DirectoryException(409, "Document version conflict."); }
    }

    public async Task<DocumentDto> UploadAsync(Actor actor, Guid? id, DocumentInput input, DocumentFile file)
    {
        await Roles(actor, true);
        Validate(input);
        var index = ValidateFile(file);
        var original = id.HasValue ? await Load(actor, id.Value, true) : null;
        var plant = original?.PlantId ?? input.PlantId;
        await directory.GetAsync(actor, "plants", plant);
        await using var tx = await db.Database.BeginTransactionAsync();
        await Lock(plant);
        await Roles(actor, true);
        await directory.GetAsync(actor, "plants", plant);
        Document d;
        string? before = null;
        if (original != null)
        {
            await db.Entry(original).ReloadAsync();
            d = await Load(actor, original.Id, true);
            Version(d, input.Version);
            before = JsonSerializer.Serialize(Dto(d));
            d.Version++;
        }
        else
        {
            if ((await directory.GetAsync(actor, "plants", plant)).IsArchived || input.IsArchived)
            {
                throw new DirectoryException(400, "Archived resources cannot receive documents.");
            }

            if (input.AssetId.HasValue)
            {
                var a = await directory.GetAsync(actor, "assets", input.AssetId.Value);
                if (a.PlantId != plant || a.IsArchived)
                {
                    throw new DirectoryException(404, "Asset not found.");
                }
            }
            d = new Document { PlantId = plant, AssetId = input.AssetId, Title = input.Title.Trim(), Category = input.Category, Visibility = input.Visibility, AllowedRoles = input.AllowedRoles?.Distinct().ToArray() ?? [] };
            if (!Allowed(d, await Roles(actor)))
            {
                throw new DirectoryException(400, "The uploading role must be included in the document ACL.");
            }

            db.Documents.Add(d);
        }
        var n = await db.DocumentVersions.Where(x => x.DocumentId == d.Id).Select(x => (int?)x.VersionNumber).MaxAsync() ?? 0;
        if (n >= 200)
        {
            throw new DirectoryException(400, "At most 200 versions per document.");
        }

        var key = await files.PutAsync(file.Content);
        var committing = false;
        try
        {
            var v = new DocumentVersion
            {
                DocumentId = d.Id,
                VersionNumber = n + 1,
                FileName = file.FileName,
                MediaType = file.MediaType,
                StorageKey = key,
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(file.Content)),
                SizeBytes = file.Content.Length,
                UploadedByUserId = actor.UserId,
                UploadedAt = clock.GetUtcNow(),
                IndexStatus = index
            };
            db.DocumentVersions.Add(v);
            Audit(actor, d, "Upload", before, new
            {
                v.Id,
                v.VersionNumber,
                v.Sha256,
                v.SizeBytes,
                v.FileName
            });
            await Save();
            committing = true;
            await tx.CommitAsync();
            return Dto(d);
        }
        // A lost commit acknowledgement can mean committed metadata. Keep the file in that case.
        catch
        {
            if (!committing)
            {
                await files.RemoveAsync(key);
            }

            throw;
        }
    }

    public async Task<DocumentDto> ConfigureAsync(Actor actor, Guid id, DocumentInput input)
    {
        Validate(input);
        var d = await Load(actor, id, true);
        await using var tx = await db.Database.BeginTransactionAsync();
        await Lock(d.PlantId);
        await db.Entry(d).ReloadAsync();
        await Load(actor, id, true);
        Version(d, input.Version);
        if (input.PlantId != d.PlantId || input.AssetId != d.AssetId)
        {
            throw new DirectoryException(400, "Document ownership cannot change.");
        }

        var before = JsonSerializer.Serialize(Dto(d));
        d.Title = input.Title.Trim();
        d.Category = input.Category;
        d.Visibility = input.Visibility;
        d.AllowedRoles = input.AllowedRoles?.Distinct().ToArray() ?? [];
        d.IsArchived = input.IsArchived;
        d.Version++;
        if (d.IsArchived)
        {
            d.CurrentPublishedVersionId = null;
        }

        Audit(actor, d, "Configure", before);
        await Save();
        await tx.CommitAsync();
        return Dto(d);
    }

    public async Task<DocumentDto> PublishAsync(Actor actor, Guid id, PublishDocument input, bool withdraw)
    {
        var d = await Load(actor, id, true);
        await using var tx = await db.Database.BeginTransactionAsync();
        await Lock(d.PlantId);
        await db.Entry(d).ReloadAsync();
        await Load(actor, id, true);
        Version(d, input.Version);
        var v = await db.DocumentVersions.SingleOrDefaultAsync(x => x.DocumentId == id && x.VersionNumber == input.VersionNumber) ?? throw new DirectoryException(404, "Version not found.");
        var before = JsonSerializer.Serialize(Dto(d));
        if (withdraw)
        {
            if (v.Status != "Published")
            {
                throw new DirectoryException(400, "Only published versions can be withdrawn.");
            }

            v.Status = "Withdrawn";
            if (d.CurrentPublishedVersionId == v.Id)
            {
                d.CurrentPublishedVersionId = null;
            }
        }
        else
        {
            if (v.Status != "Draft")
            {
                throw new DirectoryException(400, "Only draft versions can be published.");
            }

            v.Status = "Published";
            v.PublishedAt = clock.GetUtcNow();
            d.CurrentPublishedVersionId = v.Id;
        }
        d.Version++;
        Audit(actor, d, withdraw ? "Withdraw" : "Publish", before, new
        {
            v.Id,
            v.VersionNumber,
            v.Status
        });
        await Save();
        await tx.CommitAsync();
        return Dto(d);
    }

    public async Task<DocumentFile> DownloadAsync(Actor actor, Guid id, int version)
    {
        // Hold the same plant lock as ACL/withdraw writes until authorization and file read complete.
        var d = await Load(actor, id);
        await using var tx = await db.Database.BeginTransactionAsync();
        await Lock(d.PlantId);
        await db.Entry(d).ReloadAsync();
        await Load(actor, id);
        var v = await db.DocumentVersions.AsNoTracking().SingleOrDefaultAsync(x => x.DocumentId == id && x.VersionNumber == version && x.Status == "Published");
        if (v == null || !d.CurrentPublishedVersionId.HasValue)
        {
            throw new DirectoryException(404, "Published version not available.");
        }

        return new(v.FileName, v.MediaType, await files.ReadAsync(v.StorageKey));
    }
}
