using System.Security.Claims;
using PlantOps.Application.Assets;
using PlantOps.Application.Operations;

namespace PlantOps.Api;

public static class OverviewEndpoints
{
    public static void MapOverview(this WebApplication app)
    {
        app.MapGet("/api/v1/plants/{plantId:guid}/overview", (HttpContext context, IOverviewService service,
            Guid plantId, bool? unacknowledgedOnly, bool? mine, string? workOrderStatus, int? limit) =>
            service.GetAsync(new Actor(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!, context.TraceIdentifier),
                plantId, unacknowledgedOnly ?? false, mine ?? false, workOrderStatus, limit ?? 5)).RequireAuthorization();
    }
}
