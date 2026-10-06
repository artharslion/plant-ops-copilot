using PlantOps.Application.Assets;
using PlantOps.Domain.Maintenance;

namespace PlantOps.Application.Maintenance;

public sealed record CreateWorkOrder(Guid AssetId, string Title, string Description = "", string Priority = "Medium", Guid? AlarmId = null, string? AssignedToUserId = null);

public sealed record TransitionWorkOrder(long Version, string Status, string? Reason = null, string? Resolution = null);

public sealed record AppendWorkOrderNote(long Version, string Text);

public sealed record MaintenanceUser(string Id, string DisplayName);

public sealed record WorkOrderPage(IReadOnlyList<WorkOrder> Items, string? NextCursor);

public interface IWorkOrderService
{
    Task<WorkOrderPage> ListAsync(Actor actor, Guid assetId, bool history, int limit, string? cursor);

    Task<WorkOrder> GetAsync(Actor actor, Guid id);

    Task<IReadOnlyList<WorkOrderNote>> NotesAsync(Actor actor, Guid id);

    Task<IReadOnlyList<MaintenanceUser>> AssigneesAsync(Actor actor, Guid plantId);

    Task<WorkOrder> CreateAsync(Actor actor, CreateWorkOrder input);

    Task<WorkOrder> TransitionAsync(Actor actor, Guid id, TransitionWorkOrder input);

    Task<WorkOrder> NoteAsync(Actor actor, Guid id, AppendWorkOrderNote input);
}
