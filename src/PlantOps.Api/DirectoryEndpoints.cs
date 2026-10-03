using System.Security.Claims;
using PlantOps.Application.Assets;

namespace PlantOps.Api;

public static class DirectoryEndpoints
{
    public static void MapDirectory(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1").RequireAuthorization();
        static Actor Actor(HttpContext ctx) => new(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)!, ctx.TraceIdentifier);
        api.MapGet("/plants", (HttpContext ctx, IAssetDirectory directory, int? limit, string? cursor) =>
            directory.ListAsync(Actor(ctx), "plants", null, null, limit ?? 50, cursor));
        api.MapGet("/plants/{plantId:guid}/locations", (HttpContext ctx, IAssetDirectory directory, Guid plantId, int? limit, string? cursor) =>
            directory.ListAsync(Actor(ctx), "locations", plantId, null, limit ?? 50, cursor));
        api.MapGet("/assets", (HttpContext ctx, IAssetDirectory directory, Guid plantId, Guid? locationId, int? limit, string? cursor) =>
            directory.ListAsync(Actor(ctx), "assets", plantId, locationId, limit ?? 50, cursor));
        foreach (var kind in new[] { "plants", "locations", "assets" })
        {
            api.MapGet($"/{kind}/{{id:guid}}", (HttpContext ctx, IAssetDirectory directory, Guid id) =>
                directory.GetAsync(Actor(ctx), kind, id));
            api.MapPatch($"/{kind}/{{id:guid}}", (HttpContext ctx, IAssetDirectory directory, Guid id, DirectoryInput input) =>
                directory.SaveAsync(Actor(ctx), kind, id, null, input));
        }
        api.MapPost("/plants", async (HttpContext ctx, IAssetDirectory directory, DirectoryInput input) =>
        {
            var result = await directory.SaveAsync(Actor(ctx), "plants", null, null, input);
            return Results.Created($"/api/v1/plants/{result.Id}", result);
        });
        foreach (var kind in new[] { "locations", "assets" })
            api.MapPost($"/plants/{{plantId:guid}}/{kind}", async (HttpContext ctx, IAssetDirectory directory, Guid plantId, DirectoryInput input) =>
            {
                var result = await directory.SaveAsync(Actor(ctx), kind, null, plantId, input);
                return Results.Created($"/api/v1/{kind}/{result.Id}", result);
            });
    }
}
