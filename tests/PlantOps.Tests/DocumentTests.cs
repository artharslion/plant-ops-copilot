using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PlantOps.Application.Knowledge;
using PlantOps.Infrastructure.Persistence;
using Xunit;

namespace PlantOps.Tests;

[Collection("Database")]
public sealed class DocumentTests
{
    private static readonly Guid North = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid South = Guid.Parse("10000000-0000-0000-0000-000000000002");
    private static readonly Guid Pump = Guid.Parse("30000000-0000-0000-0000-000000000001");

    internal static async Task<HttpResponseMessage> Upload(AssetManagementTests.Session s, DocumentInput input, string name = "manual.md", string mime = "text/markdown", byte[]? content = null, Guid? id = null)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(JsonSerializer.Serialize(input)), "metadata");
        var bytes = new ByteArrayContent(content ?? Encoding.UTF8.GetBytes("# Original fictional pump manual\nInspect coupling with equipment stopped."));
        bytes.Headers.ContentType = new MediaTypeHeaderValue(mime);
        form.Add(bytes, "file", name);
        return await s.Client.PostAsync(id.HasValue ? $"/api/v1/documents/{id}/versions" : "/api/v1/documents", form);
    }

    internal static DocumentInput Metadata(DocumentDto d) => new(d.PlantId, d.AssetId, d.Title, d.Category, d.Visibility, d.AllowedRoles, d.Version);

    internal static async Task<DocumentDto> Publish(AssetManagementTests.Session s, DocumentDto d, int v, bool withdraw = false)
    {
        var r = await s.Client.PostAsJsonAsync($"/api/v1/documents/{d.Id}/{(withdraw ? "withdraw" : "publish")}", new PublishDocument(d.Version, v));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<DocumentDto>())!;
    }

    [DatabaseFact]
    public async Task ImmutableVersionsPublishWithdrawAndAclAreImmediate()
    {
        await using var s = new AssetManagementTests.Session();
        await s.Initialize();
        await s.Login();
        var up = await Upload(s, new(North, Pump, "Original manual"));
        Assert.Equal(HttpStatusCode.Created, up.StatusCode);
        var d = (await up.Content.ReadFromJsonAsync<DocumentDto>())!;
        var path = $"/api/v1/documents/{d.Id}/versions/1/download";
        Assert.Equal(HttpStatusCode.NotFound, (await s.Client.GetAsync(path)).StatusCode);
        d = await Publish(s, d, 1);
        var original = await s.Client.GetByteArrayAsync(path);
        var meta = (await s.Client.GetFromJsonAsync<DocumentVersionDto[]>($"/api/v1/documents/{d.Id}/versions"))!;
        Assert.Single(meta);
        Assert.DoesNotContain("storage", JsonSerializer.Serialize(meta).ToLowerInvariant());
        up = await Upload(s, Metadata(d), content: Encoding.UTF8.GetBytes("# Second version"), id: d.Id);
        d = (await up.Content.ReadFromJsonAsync<DocumentDto>())!;
        d = await Publish(s, d, 2);
        Assert.Equal(original, await s.Client.GetByteArrayAsync(path));
        await using var viewer = new AssetManagementTests.Session();
        await viewer.Initialize("Viewer");
        await viewer.Login();
        Assert.Equal(HttpStatusCode.OK, (await viewer.Client.GetAsync(path)).StatusCode);
        var restricted = Metadata(d) with
        {
            Visibility = "RestrictedRoles",
            AllowedRoles = ["Admin"]
        };
        var configured = await s.Client.PatchAsJsonAsync($"/api/v1/documents/{d.Id}", restricted);
        d = (await configured.Content.ReadFromJsonAsync<DocumentDto>())!;
        foreach (var suffix in new[] { "", "/versions", "/versions/1/download", "/versions/2/download" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await viewer.Client.GetAsync($"/api/v1/documents/{d.Id}{suffix}")).StatusCode);
        }

        Assert.DoesNotContain((await viewer.Client.GetFromJsonAsync<DocumentPage>($"/api/v1/documents?plantId={North}"))!.Items, x => x.Id == d.Id);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.PatchAsJsonAsync($"/api/v1/documents/{d.Id}", restricted)).StatusCode);
        d = await Publish(s, d, 2, true);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Client.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Client.GetAsync($"/api/v1/documents/{d.Id}/versions/2/download")).StatusCode);
        configured = await s.Client.PatchAsJsonAsync($"/api/v1/documents/{d.Id}", Metadata(d) with
        {
            IsArchived = true
        });
        Assert.Equal(HttpStatusCode.OK, configured.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Client.GetAsync($"/api/v1/documents/{d.Id}")).StatusCode);
    }

    [DatabaseFact]
    public async Task UploadValidationRolesScopeAndCsrf()
    {
        await using var s = new AssetManagementTests.Session();
        await s.Initialize();
        await s.Login();
        var input = new DocumentInput(North, Pump, "Validation");
        foreach (var f in new[] { ("manual.exe", "text/plain", new byte[] { 65 }), ("manual.pdf", "application/pdf", new byte[] { 65 }), ("manual.txt", "text/plain", new byte[] { 0xff, 0xfe }), ("manual.txt", "text/plain", new byte[] { 0 }), ("manual.md", "text/markdown", Encoding.UTF8.GetBytes("<html>fake</html>")), ("../manual.md", "text/markdown", new byte[] { 65 }), ("manual.txt", "text/plain", new byte[10485761]) })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await Upload(s, input, f.Item1, f.Item2, f.Item3)).StatusCode);
        }

        foreach (var role in new[] { "Viewer", "Operator" })
        {
            await using var r = new AssetManagementTests.Session();
            await r.Initialize(role);
            await r.Login();
            Assert.Equal(HttpStatusCode.Forbidden, (await Upload(r, input)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await Upload(s, input with
        {
            PlantId = South
        })).StatusCode);
        await using var foreign = new AssetManagementTests.Session();
        await foreign.Initialize(both: true);
        await foreign.Login();
        var up = await Upload(foreign, new(South, null, "Foreign"));
        var d = (await up.Content.ReadFromJsonAsync<DocumentDto>())!;
        foreach (var suffix in new[] { "", "/versions", "/versions/1/download" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await s.Client.GetAsync($"/api/v1/documents/{d.Id}{suffix}")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await Upload(s, Metadata(d), id: d.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Client.PostAsJsonAsync($"/api/v1/documents/{d.Id}/publish", new PublishDocument(d.Version, 1))).StatusCode);
        s.Client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(s, input)).StatusCode);
        await s.RefreshToken();
        up = await Upload(s, input, "scan.pdf", "application/pdf", Encoding.ASCII.GetBytes("%PDF-1.4\n%%EOF"));
        Assert.Equal(HttpStatusCode.Created, up.StatusCode);
        d = (await up.Content.ReadFromJsonAsync<DocumentDto>())!;
        Assert.Equal("NeedsText", Assert.Single((await s.Client.GetFromJsonAsync<DocumentVersionDto[]>($"/api/v1/documents/{d.Id}/versions"))!).IndexStatus);
    }

    [DatabaseFact]
    public async Task FailureAfterSqlRollsBackMetadataAuditOutboxAndCleansFile()
    {
        await using var s = new AssetManagementTests.Session();
        await s.Initialize();
        await s.Login();
        var up = await Upload(s, new(North, Pump, "Rollback manual"));
        var d = (await up.Content.ReadFromJsonAsync<DocumentDto>())!;
        var files = Directory.GetFiles(s.DocumentRoot).Length;
        s.Failure.Enabled = true;
        Assert.Equal(HttpStatusCode.InternalServerError, (await Upload(s, Metadata(d), id: d.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, (await s.Client.PostAsJsonAsync($"/api/v1/documents/{d.Id}/publish", new PublishDocument(d.Version, 1))).StatusCode);
        s.Failure.Enabled = false;
        Assert.Equal(files, Directory.GetFiles(s.DocumentRoot).Length);
        Assert.Equal(d.Version, (await s.Client.GetFromJsonAsync<DocumentDto>($"/api/v1/documents/{d.Id}"))!.Version);
        Assert.Single((await s.Client.GetFromJsonAsync<DocumentVersionDto[]>($"/api/v1/documents/{d.Id}/versions"))!);
        await using var scope = s.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
        Assert.Equal(1, await db.AuditLogs.CountAsync(x => x.ResourceId == d.Id));
        Assert.Single(await db.DocumentChunks.Where(x => x.DocumentId == d.Id).ToListAsync());
        d = await Publish(s, d, 1);
        Assert.Equal(HttpStatusCode.OK, (await s.Client.GetAsync($"/api/v1/documents/{d.Id}/versions/1/download")).StatusCode);
    }
}
