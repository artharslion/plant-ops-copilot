using System.Security.Claims;
using PlantOps.Application.Assets;
using PlantOps.Application.Maintenance;

namespace PlantOps.Api;

public static class WorkOrderEndpoints
{
    public static void MapWorkOrders(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1").RequireAuthorization();
        static Actor Actor(HttpContext c) => new(c.User.FindFirstValue(ClaimTypes.NameIdentifier)!, c.TraceIdentifier);
        api.MapGet("/work-orders", (HttpContext c, IWorkOrderService s, Guid assetId, bool? history, int? limit, string? cursor) => s.ListAsync(Actor(c), assetId, history ?? false, limit ?? 50, cursor));
        api.MapGet("/work-orders/{id:guid}", (HttpContext c, IWorkOrderService s, Guid id) => s.GetAsync(Actor(c), id));
        api.MapGet("/work-orders/{id:guid}/notes", (HttpContext c, IWorkOrderService s, Guid id) => s.NotesAsync(Actor(c), id));
        api.MapGet("/plants/{id:guid}/maintenance-users", (HttpContext c, IWorkOrderService s, Guid id) => s.AssigneesAsync(Actor(c), id));
        api.MapPost("/work-orders", async (HttpContext c, IWorkOrderService s, CreateWorkOrder input) =>
        {
            var w = await s.CreateAsync(Actor(c), input);
            return Results.Created($"/api/v1/work-orders/{w.Id}", w);
        });
        api.MapPost("/work-orders/{id:guid}/transition", (HttpContext c, IWorkOrderService s, Guid id, TransitionWorkOrder input) => s.TransitionAsync(Actor(c), id, input));
        api.MapPost("/work-orders/{id:guid}/notes", (HttpContext c, IWorkOrderService s, Guid id, AppendWorkOrderNote input) => s.NoteAsync(Actor(c), id, input));
    }
}
