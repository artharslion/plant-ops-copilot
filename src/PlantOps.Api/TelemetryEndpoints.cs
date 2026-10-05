using System.Security.Claims;
using PlantOps.Application.Assets;
using PlantOps.Application.Telemetry;

namespace PlantOps.Api;

public static class TelemetryEndpoints
{
    public static void MapTelemetry(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1").RequireAuthorization();
        static Actor Actor(HttpContext ctx) => new(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)!, ctx.TraceIdentifier);
        api.MapGet("/assets/{assetId:guid}/measurement-points", (HttpContext ctx, ITelemetryService service, Guid assetId) => service.PointsAsync(Actor(ctx), assetId));
        api.MapPost("/assets/{assetId:guid}/measurement-points", async (HttpContext ctx, ITelemetryService service, Guid assetId, PointInput input) =>
        {
            var point = await service.SavePointAsync(Actor(ctx), assetId, null, input);
            return Results.Created($"/api/v1/assets/{assetId}/measurement-points/{point.Id}", point);
        }).Produces<PointOverview>(201);
        api.MapPatch("/assets/{assetId:guid}/measurement-points/{pointId:guid}", (HttpContext ctx, ITelemetryService service, Guid assetId, Guid pointId, PointInput input) => service.SavePointAsync(Actor(ctx), assetId, pointId, input));
        api.MapGet("/assets/{assetId:guid}/measurements", (HttpContext ctx, ITelemetryService service, Guid assetId, Guid pointId, DateTimeOffset from, DateTimeOffset to, string? bucket) => service.TrendAsync(Actor(ctx), assetId, pointId, from, to, bucket ?? "raw"));
        api.MapGet("/plants/{plantId:guid}/data-sources", (HttpContext ctx, ITelemetryService service, Guid plantId) => service.SourcesAsync(Actor(ctx), plantId));
        api.MapPost("/plants/{plantId:guid}/data-sources", async (HttpContext ctx, ITelemetryService service, Guid plantId, SourceInput input) =>
        {
            var source = await service.SaveSourceAsync(Actor(ctx), plantId, null, input);
            return Results.Created($"/api/v1/plants/{plantId}/data-sources/{source.Id}", source);
        }).Produces<SourceDto>(201);
        api.MapPatch("/plants/{plantId:guid}/data-sources/{id:guid}", (HttpContext ctx, ITelemetryService service, Guid plantId, Guid id, SourceInput input) => service.SaveSourceAsync(Actor(ctx), plantId, id, input));
        api.MapGet("/assets/{assetId:guid}/measurement-points/{pointId:guid}/source-mappings", (HttpContext ctx, ITelemetryService service, Guid assetId, Guid pointId) => service.MappingsAsync(Actor(ctx), assetId, pointId));
        api.MapPost("/assets/{assetId:guid}/measurement-points/{pointId:guid}/source-mappings", async (HttpContext ctx, ITelemetryService service, Guid assetId, Guid pointId, MappingInput input) =>
        {
            var mapping = await service.SaveMappingAsync(Actor(ctx), assetId, pointId, null, input);
            return Results.Created($"/api/v1/assets/{assetId}/measurement-points/{pointId}/source-mappings/{mapping.Id}", mapping);
        }).Produces<MappingDto>(201);
        api.MapPatch("/assets/{assetId:guid}/measurement-points/{pointId:guid}/source-mappings/{id:guid}", (HttpContext ctx, ITelemetryService service, Guid assetId, Guid pointId, Guid id, MappingInput input) => service.SaveMappingAsync(Actor(ctx), assetId, pointId, id, input));
    }
}
