using System.Security.Claims;
using PlantOps.Application.AI;
using PlantOps.Application.Assets;

namespace PlantOps.Api;

public static class AiSettingsEndpoints
{
    public static void MapAiSettings(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1/settings/ai").RequireAuthorization(policy => policy.RequireRole("Admin"));
        static Actor Actor(HttpContext context) => new(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!, context.TraceIdentifier);
        api.MapGet("", (HttpContext context, IAiSettingsService service) => service.GetAsync(Actor(context), context.RequestAborted));
        api.MapPut("", (HttpContext context, IAiSettingsService service, AiSettingsInput input) => service.SaveAsync(Actor(context), input, context.RequestAborted));
    }
}
