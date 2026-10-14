using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PlantOps.Application.Assets;
using PlantOps.Application.Knowledge;
using PlantOps.Domain.Knowledge;
using PlantOps.Infrastructure.Identity;
using PlantOps.Infrastructure.Persistence;
using Xunit;

namespace PlantOps.Tests;

[Collection("Database")]
public sealed class DocumentEvidenceTests
{
    private static readonly Guid North = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid South = Guid.Parse("10000000-0000-0000-0000-000000000002");
    private static readonly Guid Pump = Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly Guid Fan = Guid.Parse("30000000-0000-0000-0000-000000000002");

    private static string SearchPath(Guid asset, string term, int limit = 6) => $"/api/v1/assets/{asset}/document-evidence?query={Uri.EscapeDataString(term)}&limit={limit}";

    private static async Task<DocumentDto> Upload(AssetManagementTests.Session s, string text, Guid? asset = null, Guid? plant = null)
    {
        var r = await DocumentTests.Upload(s, new(plant ?? North, asset, "Test evidence"), content: Encoding.UTF8.GetBytes(text));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<DocumentDto>())!;
    }

    [DatabaseFact]
    public async Task OnlyCurrentPublishedVersionIsSearchableAndOldCitationExpires()
    {
        await using var s = new AssetManagementTests.Session();
        await s.Initialize();
        await s.Login();
        var term = "coupling" + Guid.NewGuid().ToString("N");
        var d = await Upload(s, $"# P-101\n\n## 振动检查\n{term} 12.6 mm/s", Pump);
        Assert.Empty((await s.Client.GetFromJsonAsync<DocumentSearchResult>(SearchPath(Pump, term)))!.Items);
        d = await DocumentTests.Publish(s, d, 1);
        var response = await s.Client.GetAsync(SearchPath(Pump, term));
        Assert.True(response.Headers.CacheControl!.NoStore);
        var result = (await response.Content.ReadFromJsonAsync<DocumentSearchResult>())!;
        var e = Assert.Single(result.Items);
        Assert.Equal("P-101 / 振动检查", e.Section);
        Assert.Equal(1, e.VersionNumber);
        Assert.Equal(d.CurrentPublishedVersionId, e.VersionId);
        Assert.Equal(TextDocumentParser.Version, e.ParserVersion);
        Assert.Equal(e.Text, (await s.Client.GetFromJsonAsync<DocumentEvidence>(e.CitationPath))!.Text);
        Assert.DoesNotContain("storage", JsonSerializer.Serialize(result).ToLowerInvariant());
        Assert.DoesNotContain("uploadedBy", JsonSerializer.Serialize(result));
        var up = await DocumentTests.Upload(s, DocumentTests.Metadata(d), content: Encoding.UTF8.GetBytes($"{term} 第二版"), id: d.Id);
        d = (await up.Content.ReadFromJsonAsync<DocumentDto>())!;
        Assert.Equal(1, Assert.Single((await s.Client.GetFromJsonAsync<DocumentSearchResult>(SearchPath(Pump, term)))!.Items).VersionNumber);
        d = await DocumentTests.Publish(s, d, 2);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Client.GetAsync(e.CitationPath)).StatusCode);
        Assert.Equal(2, Assert.Single((await s.Client.GetFromJsonAsync<DocumentSearchResult>(SearchPath(Pump, term)))!.Items).VersionNumber);
        await DocumentTests.Publish(s, d, 2, true);
        Assert.Empty((await s.Client.GetFromJsonAsync<DocumentSearchResult>(SearchPath(Pump, term)))!.Items);
    }

    [DatabaseFact]
    public async Task PlantAssetAclAndForgedCitationCannotBypassScope()
    {
        await using var admin = new AssetManagementTests.Session();
        await admin.Initialize(both: true);
        await admin.Login();
        var term = "scope" + Guid.NewGuid().ToString("N");
        var shared = await DocumentTests.Publish(admin, await Upload(admin, term), 1);
        var otherAsset = await admin.Create("assets", North);
        await DocumentTests.Publish(admin, await Upload(admin, term, otherAsset.Id), 1);
        await DocumentTests.Publish(admin, await Upload(admin, term, Fan, South), 1);
        var restricted = await DocumentTests.Publish(admin, await Upload(admin, term, Pump), 1);
        var patch = await admin.Client.PatchAsJsonAsync($"/api/v1/documents/{restricted.Id}", DocumentTests.Metadata(restricted) with
        {
            Visibility = "RestrictedRoles",
            AllowedRoles = ["Admin"]
        });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        await using var viewer = new AssetManagementTests.Session();
        await viewer.Initialize("Viewer");
        await viewer.Login();
        var e = Assert.Single((await viewer.Client.GetFromJsonAsync<DocumentSearchResult>(SearchPath(Pump, term)))!.Items);
        Assert.Equal(shared.Id, e.DocumentId);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.Client.GetAsync(SearchPath(Fan, term))).StatusCode);
        var adminResult = (await admin.Client.GetFromJsonAsync<DocumentSearchResult>(SearchPath(Pump, term)))!;
        var secret = Assert.Single(adminResult.Items, x => x.DocumentId == restricted.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.Client.GetAsync(secret.CitationPath)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.Client.GetAsync(e.CitationPath.Replace(e.ChunkId.ToString(), Guid.NewGuid().ToString(), StringComparison.Ordinal))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.Client.GetAsync(secret.CitationPath.Replace(Pump.ToString(), otherAsset.Id.ToString(), StringComparison.Ordinal))).StatusCode);
        await using var anon = new AssetManagementTests.Session();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.Client.GetAsync(SearchPath(Pump, term))).StatusCode);
    }

    [DatabaseFact]
    public async Task AclRolePlantAndEnabledRevocationsInvalidateEvenSameScopedService()
    {
        await using var admin = new AssetManagementTests.Session();
        await admin.Initialize();
        await admin.Login();
        var term = "revoke" + Guid.NewGuid().ToString("N");
        var d = await DocumentTests.Publish(admin, await Upload(admin, term, Pump), 1);
        await using var reader = new AssetManagementTests.Session();
        await reader.Initialize("Viewer");
        await using var longScope = reader.Factory.Services.CreateAsyncScope();
        var service = longScope.ServiceProvider.GetRequiredService<IDocumentEvidenceService>();
        var actor = new Actor(reader.UserId, "evidence-revocation-test");
        var e = Assert.Single((await service.SearchAsync(actor, Pump, term)).Items);
        // Prime tracked Identity state, then revoke through a separate scope.
        await longScope.ServiceProvider.GetRequiredService<UserManager<PlantOpsUser>>().FindByIdAsync(reader.UserId);
        var patched = await admin.Client.PatchAsJsonAsync($"/api/v1/documents/{d.Id}", DocumentTests.Metadata(d) with
        {
            Visibility = "RestrictedRoles",
            AllowedRoles = ["Admin"]
        });
        d = (await patched.Content.ReadFromJsonAsync<DocumentDto>())!;
        Assert.Empty((await service.SearchAsync(actor, Pump, term)).Items);
        Assert.Equal(404, (await Assert.ThrowsAsync<DirectoryException>(() => service.ExpandAsync(actor, Pump, e.DocumentId, e.VersionId, e.ChunkId))).Status);
        patched = await admin.Client.PatchAsJsonAsync($"/api/v1/documents/{d.Id}", DocumentTests.Metadata(d) with
        {
            Visibility = "PlantReaders",
            AllowedRoles = []
        });
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        await using var changeScope = reader.Factory.Services.CreateAsyncScope();
        var users = changeScope.ServiceProvider.GetRequiredService<UserManager<PlantOpsUser>>();
        var user = (await users.FindByIdAsync(reader.UserId))!;
        Assert.True((await users.RemoveFromRoleAsync(user, "Viewer")).Succeeded);
        Assert.Equal(403, (await Assert.ThrowsAsync<DirectoryException>(() => service.ExpandAsync(actor, Pump, e.DocumentId, e.VersionId, e.ChunkId))).Status);
        Assert.True((await users.AddToRoleAsync(user, "Viewer")).Succeeded);
        var db = changeScope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
        // Revoke and restore only the grant created for this test user.
        var grant = await db.UserPlantAccess.SingleAsync(x => x.UserId == reader.UserId && x.PlantId == North);
        db.UserPlantAccess.Remove(grant);
        await db.SaveChangesAsync();
        Assert.Equal(404, (await Assert.ThrowsAsync<DirectoryException>(() => service.ExpandAsync(actor, Pump, e.DocumentId, e.VersionId, e.ChunkId))).Status);
        db.UserPlantAccess.Add(grant);
        await db.SaveChangesAsync();
        user.IsEnabled = false;
        Assert.True((await users.UpdateAsync(user)).Succeeded);
        Assert.Equal(401, (await Assert.ThrowsAsync<DirectoryException>(() => service.SearchAsync(actor, Pump, term))).Status);
        Assert.Equal(401, (await Assert.ThrowsAsync<DirectoryException>(() => service.ExpandAsync(actor, Pump, e.DocumentId, e.VersionId, e.ChunkId))).Status);
    }

    [DatabaseFact]
    public async Task LegacyIndexIsIdempotentAndChecksSourceHash()
    {
        await using var s = new AssetManagementTests.Session();
        await s.Initialize();
        await s.Login();
        var term = "legacy" + Guid.NewGuid().ToString("N");
        await using var scope = s.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentFileStore>();
        var bytes = Encoding.UTF8.GetBytes($"# 旧资料\n\n{term} 振动 coupling");
        var d = new Document { PlantId = North, AssetId = Pump, Title = "Legacy original" };
        var v = new DocumentVersion { DocumentId = d.Id, VersionNumber = 1, FileName = "legacy.txt", MediaType = "text/plain", Status = "Published", SizeBytes = bytes.Length, Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), StorageKey = await store.PutAsync(bytes), UploadedByUserId = s.UserId, UploadedAt = DateTimeOffset.UtcNow };
        d.CurrentPublishedVersionId = v.Id;
        db.AddRange(d, v);
        var corrupt = new Document { PlantId = North, AssetId = Pump, Title = "Corrupt legacy" };
        var badVersion = new DocumentVersion { DocumentId = corrupt.Id, VersionNumber = 1, FileName = "corrupt.txt", MediaType = "text/plain", Status = "Published", SizeBytes = bytes.Length, Sha256 = new string('0', 64), StorageKey = await store.PutAsync(bytes), UploadedByUserId = s.UserId, UploadedAt = DateTimeOffset.UtcNow };
        corrupt.CurrentPublishedVersionId = badVersion.Id;
        db.AddRange(corrupt, badVersion);
        await db.SaveChangesAsync();
        Assert.Equal(bytes, await store.ReadBoundedAsync(v.StorageKey, bytes.Length));
        await Assert.ThrowsAsync<IOException>(() => store.ReadBoundedAsync(v.StorageKey, bytes.Length - 1));
        using var cancelledRead = new CancellationTokenSource();
        cancelledRead.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReadBoundedAsync(v.StorageKey, bytes.Length, cancelledRead.Token));
        var first = (await s.Client.GetFromJsonAsync<DocumentSearchResult>(SearchPath(Pump, term)))!;
        var second = (await s.Client.GetFromJsonAsync<DocumentSearchResult>(SearchPath(Pump, term)))!;
        Assert.Equal(Assert.Single(first.Items).ChunkId, Assert.Single(second.Items).ChunkId);
        Assert.NotEmpty(first.Warnings);
        await db.Entry(v).ReloadAsync();
        await db.Entry(badVersion).ReloadAsync();
        Assert.Equal("Ready", v.IndexStatus);
        Assert.Equal("SourceMismatch", badVersion.IndexStatus);
        Assert.Equal(TextDocumentParser.Version, v.ParserVersion);
        Assert.Equal(2, await db.DocumentChunks.CountAsync(x => x.VersionId == v.Id));
        Assert.False(await db.DocumentChunks.AnyAsync(x => x.VersionId == badVersion.Id));
    }

    [DatabaseFact]
    public async Task UnsupportedPdfOversizeAndEmptyHaveExplicitWarningsAndNoEvidence()
    {
        await using var s = new AssetManagementTests.Session();
        await s.Initialize();
        await s.Login();
        var term = "missing" + Guid.NewGuid().ToString("N");
        var pdf = await DocumentTests.Upload(s, new(North, Pump, term), "scan.pdf", "application/pdf", "%PDF-1.4\n%%EOF"u8.ToArray());
        await DocumentTests.Publish(s, (await pdf.Content.ReadFromJsonAsync<DocumentDto>())!, 1);
        await DocumentTests.Publish(s, await Upload(s, new string('a', TextDocumentParser.MaximumBytes + 1), Pump), 1);
        await DocumentTests.Publish(s, await Upload(s, " \n ", Pump), 1);
        var result = (await s.Client.GetFromJsonAsync<DocumentSearchResult>(SearchPath(Pump, term)))!;
        Assert.Empty(result.Items);
        Assert.Contains(result.Warnings, x => x.Contains("PDF", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, x => x.Contains("上限", StringComparison.Ordinal));
    }

    [DatabaseFact]
    public async Task QueryLimitsCancellationAndRankingAreDeterministic()
    {
        await using var s = new AssetManagementTests.Session();
        await s.Initialize();
        await s.Login();
        var term = "rank" + Guid.NewGuid().ToString("N");
        await DocumentTests.Publish(s, await Upload(s, $"# 振动 {term}\n\n{term} coupling\n\n{term} 温度", Pump), 1);
        var a = (await s.Client.GetFromJsonAsync<DocumentSearchResult>(SearchPath(Pump, term + " COUPLING", 1)))!;
        var b = (await s.Client.GetFromJsonAsync<DocumentSearchResult>(SearchPath(Pump, term + " COUPLING", 1)))!;
        Assert.Contains("coupling", Assert.Single(a.Items).Text);
        Assert.Equal(a.Items.ToArray(), b.Items.ToArray());
        foreach (var query in new[] { " ", "!!!", new string('x', 201), string.Join(" ", Enumerable.Range(0, 13)) })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.GetAsync(SearchPath(Pump, query))).StatusCode);
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.GetAsync(SearchPath(Pump, term, 7))).StatusCode);
        await using var scope = s.Factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IDocumentEvidenceService>();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SearchAsync(new(s.UserId, "cancel"), Pump, term, cancellationToken: cancelled.Token));
        var e = Assert.Single(a.Items);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ExpandAsync(new(s.UserId, "cancel"), Pump, e.DocumentId, e.VersionId, e.ChunkId, cancelled.Token));
    }

    [DatabaseFact]
    public async Task ArchiveInvalidatesCitationButPreservesVersionAndChunks()
    {
        await using var s = new AssetManagementTests.Session();
        await s.Initialize();
        await s.Login();
        var term = "archive" + Guid.NewGuid().ToString("N");
        var d = await DocumentTests.Publish(s, await Upload(s, term, Pump), 1);
        var e = Assert.Single((await s.Client.GetFromJsonAsync<DocumentSearchResult>(SearchPath(Pump, term)))!.Items);
        var response = await s.Client.PatchAsJsonAsync($"/api/v1/documents/{d.Id}", DocumentTests.Metadata(d) with
        {
            IsArchived = true
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty((await s.Client.GetFromJsonAsync<DocumentSearchResult>(SearchPath(Pump, term)))!.Items);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Client.GetAsync(e.CitationPath)).StatusCode);
        await using var scope = s.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
        Assert.True(await db.DocumentVersions.AnyAsync(x => x.Id == e.VersionId));
        Assert.True(await db.DocumentChunks.AnyAsync(x => x.Id == e.ChunkId));
    }

    [DatabaseFact]
    public async Task CandidateAndLegacyReadBoundsReportGapsAndBackfillProgresses()
    {
        await using var s = new AssetManagementTests.Session(connection: await TestDatabase.IsolatedConnection());
        await s.Initialize();
        await s.Login();
        await using var scope = s.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
        var files = scope.ServiceProvider.GetRequiredService<IDocumentFileStore>();
        const string term = "bounded";
        var bytes = Encoding.UTF8.GetBytes(term + new string('a', TextDocumentParser.MaximumBytes - term.Length));
        for (var i = 0; i < 5; i++)
        {
            var document = new Document { PlantId = North, AssetId = Pump, Title = "Legacy source" };
            var version = new DocumentVersion { DocumentId = document.Id, VersionNumber = 1, FileName = "bounded.txt", MediaType = "text/plain", Status = "Published", SizeBytes = bytes.Length, Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), StorageKey = await files.PutAsync(bytes), UploadedByUserId = s.UserId, UploadedAt = DateTimeOffset.UtcNow };
            document.CurrentPublishedVersionId = version.Id;
            db.AddRange(document, version);
        }
        await db.SaveChangesAsync();
        var first = (await s.Client.GetFromJsonAsync<DocumentSearchResult>(SearchPath(Pump, term)))!;
        // Existing seeded manuals also count toward the 1 MiB read budget.
        Assert.InRange(first.Items.Count, 1, 4);
        Assert.Contains(first.Warnings, x => x.Contains("旧资料", StringComparison.Ordinal));
        var second = (await s.Client.GetFromJsonAsync<DocumentSearchResult>(SearchPath(Pump, term)))!;
        Assert.Equal(5, second.Items.Count);
        Assert.Empty(second.Warnings);
        for (var i = 0; i < 46; i++)
        {
            // New tiny legacy sources join the existing five, exceeding the 50-document bound.
            var document = new Document { PlantId = North, AssetId = Pump, Title = "Small bounded" };
            var version = new DocumentVersion { DocumentId = document.Id, VersionNumber = 1, FileName = "small.txt", MediaType = "text/plain", Status = "Published", SizeBytes = term.Length, Sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(term))), StorageKey = await files.PutAsync(Encoding.UTF8.GetBytes(term)), UploadedByUserId = s.UserId, UploadedAt = DateTimeOffset.UtcNow };
            document.CurrentPublishedVersionId = version.Id;
            db.AddRange(document, version);
        }
        await db.SaveChangesAsync();
        var capped = (await s.Client.GetFromJsonAsync<DocumentSearchResult>(SearchPath(Pump, term)))!;
        Assert.Equal(6, capped.Items.Count);
        Assert.Contains(capped.Warnings, x => x.Contains("检索上限", StringComparison.Ordinal));
    }
}
