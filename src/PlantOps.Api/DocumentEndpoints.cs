using System.Security.Claims;
using System.Text.Json;
using PlantOps.Application.Assets;
using PlantOps.Application.Knowledge;

namespace PlantOps.Api;

public static class DocumentEndpoints
{
    public static void MapDocuments(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1").RequireAuthorization();
        static Actor Actor(HttpContext c) => new(c.User.FindFirstValue(ClaimTypes.NameIdentifier)!, c.TraceIdentifier);
        static async Task<IResult> Upload(HttpContext c, IDocumentService s, Guid? id)
        {
            if (!c.Request.HasFormContentType)
            {
                throw new DirectoryException(400, "Multipart upload required.");
            }

            var form = await c.Request.ReadFormAsync();
            var file = form.Files.GetFile("file");
            if (file == null || file.Length is < 1 or > 10485760 || form.Files.Count != 1)
            {
                throw new DirectoryException(400, "One file up to 10 MiB required.");
            }

            DocumentInput input;
            try
            {
                input = JsonSerializer.Deserialize<DocumentInput>(form["metadata"].ToString(), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new JsonException();
            }
            catch (JsonException) { throw new DirectoryException(400, "Invalid document metadata."); }
            await using var bytes = new MemoryStream();
            await file.CopyToAsync(bytes);
            var d = await s.UploadAsync(Actor(c), id, input, new(file.FileName, file.ContentType, bytes.ToArray()));
            return Results.Created($"/api/v1/documents/{d.Id}", d);
        }
        api.MapGet("/documents", (HttpContext c, IDocumentService s, Guid plantId, Guid? assetId, int? limit, string? cursor) => s.ListAsync(Actor(c), plantId, assetId, limit ?? 50, cursor));
        api.MapGet("/documents/{id:guid}", (HttpContext c, IDocumentService s, Guid id) => s.GetAsync(Actor(c), id));
        api.MapGet("/documents/{id:guid}/versions", (HttpContext c, IDocumentService s, Guid id) => s.VersionsAsync(Actor(c), id));
        api.MapPost("/documents", (HttpContext c, IDocumentService s) => Upload(c, s, null));
        api.MapPost("/documents/{id:guid}/versions", (HttpContext c, IDocumentService s, Guid id) => Upload(c, s, id));
        api.MapPatch("/documents/{id:guid}", (HttpContext c, IDocumentService s, Guid id, DocumentInput input) => s.ConfigureAsync(Actor(c), id, input));
        api.MapPost("/documents/{id:guid}/publish", (HttpContext c, IDocumentService s, Guid id, PublishDocument input) => s.PublishAsync(Actor(c), id, input, false));
        api.MapPost("/documents/{id:guid}/withdraw", (HttpContext c, IDocumentService s, Guid id, PublishDocument input) => s.PublishAsync(Actor(c), id, input, true));
        api.MapGet("/documents/{id:guid}/versions/{version:int}/download", async (HttpContext c, IDocumentService s, Guid id, int version) =>
        {
            c.Response.Headers.CacheControl = "no-store";
            c.Response.Headers["X-Content-Type-Options"] = "nosniff";
            var f = await s.DownloadAsync(Actor(c), id, version);
            return Results.File(f.Content, f.MediaType, f.FileName);
        });
    }
}
