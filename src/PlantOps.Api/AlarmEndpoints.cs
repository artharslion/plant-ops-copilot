using System.Security.Claims;
using PlantOps.Application.Alarms;
using PlantOps.Application.Assets;

namespace PlantOps.Api;

public static class AlarmEndpoints
{
    public static void MapAlarms(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1").RequireAuthorization();
        static Actor Actor(HttpContext ctx) => new(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)!, ctx.TraceIdentifier);
        api.MapGet("/alarms", (HttpContext ctx, IAlarmService service, Guid plantId, bool? active, Guid? assetId, int? limit, string? cursor) =>
            service.ListAsync(Actor(ctx), plantId, active, assetId, limit ?? 50, cursor));
        api.MapGet("/alarms/{id:guid}", (HttpContext ctx, IAlarmService service, Guid id) => service.GetAsync(Actor(ctx), id));
        api.MapGet("/alarms/{id:guid}/events", (HttpContext ctx, IAlarmService service, Guid id) => service.EventsAsync(Actor(ctx), id));
        api.MapPost("/alarms/{id:guid}/acknowledge", (HttpContext ctx, IAlarmService service, Guid id, AcknowledgeInput input) => service.AcknowledgeAsync(Actor(ctx), id, input.Version));
        api.MapGet("/assets/{assetId:guid}/measurement-points/{pointId:guid}/alarm-rules", (HttpContext ctx, IAlarmService service, Guid assetId, Guid pointId) => service.RulesAsync(Actor(ctx), assetId, pointId));
        api.MapPost("/assets/{assetId:guid}/measurement-points/{pointId:guid}/alarm-rules", async (HttpContext ctx, IAlarmService service, Guid assetId, Guid pointId, RuleInput input) =>
        {
            var rule = await service.SaveRuleAsync(Actor(ctx), assetId, pointId, null, input);
            return Results.Created($"/api/v1/assets/{assetId}/measurement-points/{pointId}/alarm-rules/{rule.Id}", rule);
        }).Produces<RuleDto>(201);
        api.MapPatch("/assets/{assetId:guid}/measurement-points/{pointId:guid}/alarm-rules/{id:guid}", (HttpContext ctx, IAlarmService service, Guid assetId, Guid pointId, Guid id, RuleInput input) => service.SaveRuleAsync(Actor(ctx), assetId, pointId, id, input));
    }
}
