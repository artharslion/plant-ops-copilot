using System.Security.Claims;
using PlantOps.Application.AI;
using PlantOps.Application.Assets;

namespace PlantOps.Api;

public static class ActionProposalEndpoints
{
    public static void MapActionProposals(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1").RequireAuthorization();
        static Actor Actor(HttpContext context)
        {
            context.Response.Headers.CacheControl = "no-store";
            return new(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!, context.TraceIdentifier);
        }
        api.MapPost("/investigations/{id:guid}/proposals", (HttpContext context, IActionProposalService service, Guid id) =>
            service.CreateAsync(Actor(context), id, context.RequestAborted));
        api.MapPost("/investigations/{id:guid}/note-proposals", (HttpContext context, IActionProposalService service, Guid id, CreateNoteProposal input) =>
            service.CreateNoteAsync(Actor(context), id, input, context.RequestAborted));
        api.MapGet("/investigations/{id:guid}/proposals", (HttpContext context, IActionProposalService service, Guid id) =>
            service.ListAsync(Actor(context), id, context.RequestAborted));
        api.MapGet("/action-proposals/{id:guid}", (HttpContext context, IActionProposalService service, Guid id) =>
            service.GetAsync(Actor(context), id, context.RequestAborted));
        api.MapPatch("/action-proposals/{id:guid}", (HttpContext context, IActionProposalService service, Guid id, EditActionProposal input) =>
            service.EditAsync(Actor(context), id, input, context.RequestAborted));
        api.MapPatch("/action-proposals/{id:guid}/note", (HttpContext context, IActionProposalService service, Guid id, EditNoteProposal input) =>
            service.EditNoteAsync(Actor(context), id, input, context.RequestAborted));
        api.MapPost("/action-proposals/{id:guid}/reject", (HttpContext context, IActionProposalService service, Guid id, RejectActionProposal input) =>
            service.RejectAsync(Actor(context), id, input, context.RequestAborted));
        api.MapPost("/action-proposals/{id:guid}/execute", (HttpContext context, IActionProposalService service, Guid id, ConfirmActionProposal input) =>
            service.ExecuteAsync(Actor(context), id, input, context.RequestAborted));
    }
}
