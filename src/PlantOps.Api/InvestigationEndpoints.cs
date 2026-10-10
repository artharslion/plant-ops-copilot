using System.Security.Claims;
using PlantOps.Application.AI;
using PlantOps.Application.Assets;

namespace PlantOps.Api;

public static class InvestigationEndpoints
{
    public static void MapInvestigations(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1").RequireAuthorization();
        static Actor Actor(HttpContext context) => new(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!, context.TraceIdentifier);
        api.MapGet("/investigation-capabilities", (HttpContext context, IConfiguration configuration) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var provider = configuration["AI:Provider"] ?? "Fake";
            return new
            {
                provider,
                model = provider == "Fake" ? "fake" : configuration["AI:Model"] ?? "gpt-6.1-sol",
                externalData = provider != "Fake"
            };
        });
        api.MapPost("/alarms/{alarmId:guid}/investigations", async (HttpContext context, IInvestigationService service, Guid alarmId) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var run = await service.CreateAsync(Actor(context), alarmId, context.RequestAborted);
            return Results.Accepted($"/api/v1/investigations/{run.Id}", run);
        });
        api.MapGet("/alarms/{alarmId:guid}/investigations", (HttpContext context, IInvestigationService service, Guid alarmId) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return service.ListAsync(Actor(context), alarmId, context.RequestAborted);
        });
        api.MapGet("/investigations/{id:guid}", (HttpContext context, IInvestigationService service, Guid id) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return service.GetAsync(Actor(context), id, context.RequestAborted);
        });
        api.MapPost("/investigations/{id:guid}/cancel", (HttpContext context, IInvestigationService service, Guid id) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return service.CancelAsync(Actor(context), id, context.RequestAborted);
        });
        api.MapGet("/investigations/{id:guid}/evidence/{evidenceId}", (HttpContext context, IInvestigationService service, Guid id, string evidenceId) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return service.ExpandAsync(Actor(context), id, evidenceId, context.RequestAborted);
        });
    }
}
