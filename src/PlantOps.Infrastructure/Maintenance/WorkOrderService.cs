using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using PlantOps.Application.Assets;
using PlantOps.Application.Maintenance;
using PlantOps.Domain.Identity;
using PlantOps.Domain.Maintenance;
using PlantOps.Infrastructure.Identity;
using PlantOps.Infrastructure.Persistence;

namespace PlantOps.Infrastructure.Maintenance;

public sealed class WorkOrderService(
    PlantOpsDbContext db,
    IAssetDirectory directory,
    UserManager<PlantOpsUser> users,
    TimeProvider clock) : IWorkOrderService
{
    private async Task RequireMaintenanceRoleAsync(Actor actor)
    {
        var user = await users.FindByIdAsync(actor.UserId);
        if (user is not { IsEnabled: true })
        {
            throw new DirectoryException(401, "Session unavailable.");
        }

        if (!(await users.GetRolesAsync(user)).Intersect(new[] { "Admin", "MaintenanceEngineer" }).Any())
        {
            throw new DirectoryException(403, "Maintenance role required.");
        }
    }

    public async Task<IReadOnlyList<MaintenanceUser>> AssigneesAsync(Actor actor, Guid plantId)
    {
        await directory.GetAsync(actor, "plants", plantId);
        var authorizedUserIds = db.UserPlantAccess
            .Where(x => x.PlantId == plantId)
            .Select(x => x.UserId);
        var maintenanceRoleIds = db.Roles
            .Where(x => x.Name == "Admin" || x.Name == "MaintenanceEngineer")
            .Select(x => x.Id);
        var maintenanceUserIds = db.UserRoles
            .Where(x => maintenanceRoleIds.Contains(x.RoleId))
            .Select(x => x.UserId);

        return await db.Users.AsNoTracking()
            .Where(x => x.IsEnabled && authorizedUserIds.Contains(x.Id) && maintenanceUserIds.Contains(x.Id))
            .OrderBy(x => x.DisplayName)
            .ThenBy(x => x.Id)
            .Select(x => new MaintenanceUser(x.Id, x.DisplayName))
            .ToListAsync();
    }

    public async Task<WorkOrder> GetAsync(Actor actor, Guid id)
    {
        var workOrder = await db.WorkOrders.SingleOrDefaultAsync(x => x.Id == id)
            ?? throw new DirectoryException(404, "Work order not found.");
        await directory.GetAsync(actor, "assets", workOrder.AssetId);
        return workOrder;
    }

    public async Task<IReadOnlyList<WorkOrderNote>> NotesAsync(Actor actor, Guid id)
    {
        await GetAsync(actor, id);
        return await db.WorkOrderNotes.AsNoTracking()
            .Where(x => x.WorkOrderId == id)
            .OrderBy(x => x.OccurredAt)
            .ThenBy(x => x.Id)
            .ToListAsync();
    }

    public async Task<WorkOrderPage> ListAsync(Actor actor, Guid assetId, bool history, int limit, string? cursor)
    {
        await directory.GetAsync(actor, "assets", assetId);
        if (limit is < 1 or > 200 || cursor?.Length > 128)
        {
            throw new DirectoryException(400, "Invalid page size or cursor.");
        }

        var query = db.WorkOrders.AsNoTracking()
            .Where(x => x.AssetId == assetId && (!history || x.Status == "Completed" || x.Status == "Closed"));
        if (cursor != null)
        {
            var (createdAt, workOrderId) = DecodeCursor(cursor);
            query = query.Where(x => x.CreatedAt < createdAt || x.CreatedAt == createdAt && x.Id.CompareTo(workOrderId) < 0);
        }

        var rows = await query.OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Take(limit + 1)
            .ToListAsync();
        var page = rows.Take(limit).ToList();
        var nextCursor = rows.Count > limit ? EncodeCursor(page[^1]) : null;
        return new(page, nextCursor);
    }

    private static (DateTimeOffset CreatedAt, Guid WorkOrderId) DecodeCursor(string cursor)
    {
        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split(':');
            if (parts.Length != 2)
            {
                throw new FormatException();
            }

            var createdAt = new DateTimeOffset(long.Parse(parts[0]), TimeSpan.Zero);
            var workOrderId = Guid.ParseExact(parts[1], "N");
            return (createdAt, workOrderId);
        }
        catch (Exception exception) when (exception is FormatException or OverflowException or ArgumentOutOfRangeException)
        {
            throw new DirectoryException(400, "Invalid cursor.");
        }
    }

    private static string EncodeCursor(WorkOrder workOrder) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{workOrder.CreatedAt.UtcTicks}:{workOrder.Id:N}"));

    private static void ValidateText(string? value, int maxLength, bool required = false)
    {
        if (value?.Length > maxLength || required && string.IsNullOrWhiteSpace(value))
        {
            throw new DirectoryException(400, "Missing or oversized text.");
        }
    }

    private async Task LockPlantAsync(Guid plantId) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({plantId.ToString()}, 0))");

    private void RecordWorkOrderAudit(Actor actor, WorkOrder workOrder, string action, string? before)
    {
        db.AuditLogs.Add(new AuditLog
        {
            PlantId = workOrder.PlantId,
            ResourceId = workOrder.Id,
            ResourceType = "WorkOrder",
            Action = action,
            ActorUserId = actor.UserId,
            Before = before,
            After = JsonSerializer.Serialize(workOrder),
            CorrelationId = actor.CorrelationId,
            OccurredAt = clock.GetUtcNow()
        });
    }

    private async Task SaveChangesAsync()
    {
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new DirectoryException(409, "Work order version conflict.");
        }
    }

    public async Task<WorkOrder> CreateAsync(Actor actor, CreateWorkOrder input)
    {
        await RequireMaintenanceRoleAsync(actor);
        var asset = await directory.GetAsync(actor, "assets", input.AssetId);
        ValidateText(input.Title, 200, true);
        ValidateText(input.Description, 8000);
        if (input.Priority is not ("Low" or "Medium" or "High"))
        {
            throw new DirectoryException(400, "Invalid priority.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync();
        await LockPlantAsync(asset.PlantId);

        // Access and archive state may have changed while this request waited for the plant lock.
        await RequireMaintenanceRoleAsync(actor);
        asset = await directory.GetAsync(actor, "assets", input.AssetId);
        if (asset.IsArchived || (await directory.GetAsync(actor, "plants", asset.PlantId)).IsArchived)
        {
            throw new DirectoryException(400, "Archived resources cannot receive new work orders.");
        }

        if (input.AlarmId.HasValue && !await db.Alarms.AnyAsync(
            x => x.Id == input.AlarmId && x.PlantId == asset.PlantId && x.AssetId == asset.Id))
        {
            throw new DirectoryException(404, "Associated alarm not found on this asset.");
        }

        if (input.AssignedToUserId != null && !(await AssigneesAsync(actor, asset.PlantId)).Any(x => x.Id == input.AssignedToUserId))
        {
            throw new DirectoryException(400, "Assignee requires enabled maintenance access to this plant.");
        }

        var workOrder = new WorkOrder
        {
            PlantId = asset.PlantId,
            AssetId = asset.Id,
            AlarmId = input.AlarmId,
            Title = input.Title.Trim(),
            Description = input.Description,
            Priority = input.Priority,
            AssignedToUserId = input.AssignedToUserId,
            CreatedByUserId = actor.UserId,
            CreatedAt = clock.GetUtcNow()
        };
        workOrder.Number = $"WO-{workOrder.Id:N}";
        db.WorkOrders.Add(workOrder);
        RecordWorkOrderAudit(actor, workOrder, "Create", null);
        await SaveChangesAsync();
        await transaction.CommitAsync();
        return workOrder;
    }

    private async Task<WorkOrder> GetForUpdateAsync(Actor actor, Guid id, long version)
    {
        await RequireMaintenanceRoleAsync(actor);
        var workOrder = await GetAsync(actor, id);
        await LockPlantAsync(workOrder.PlantId);

        // Recheck access and reload the version under the lock before applying a business change.
        await RequireMaintenanceRoleAsync(actor);
        await directory.GetAsync(actor, "assets", workOrder.AssetId);
        await db.Entry(workOrder).ReloadAsync();
        if (workOrder.Version != version)
        {
            throw new DirectoryException(409, "Work order version conflict.");
        }

        return workOrder;
    }

    public async Task<WorkOrder> TransitionAsync(Actor actor, Guid id, TransitionWorkOrder input)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        var workOrder = await GetForUpdateAsync(actor, id, input.Version);
        ValidateText(input.Reason, 8000);
        ValidateText(input.Resolution, 8000);
        var resolution = input.Status == "Completed" ? input.Resolution : workOrder.Resolution;
        var error = WorkOrderState.Validate(workOrder.Status, input.Status, input.Reason, resolution);
        if (error != null)
        {
            throw new DirectoryException(400, error);
        }

        var before = JsonSerializer.Serialize(workOrder);
        if (input.Reason != null)
        {
            db.WorkOrderNotes.Add(new WorkOrderNote
            {
                WorkOrderId = id,
                Text = input.Reason,
                Kind = TransitionNoteKind(workOrder.Status, input.Status),
                ActorUserId = actor.UserId,
                OccurredAt = clock.GetUtcNow()
            });
        }

        workOrder.Status = input.Status;
        workOrder.Version++;
        if (input.Status == "Completed")
        {
            workOrder.CompletedAt = clock.GetUtcNow();
            workOrder.Resolution = input.Resolution!.Trim();
        }

        if (input.Status == "InProgress")
        {
            workOrder.CompletedAt = null;
            workOrder.Resolution = null;
        }

        if (input.Status == "Closed")
        {
            workOrder.ClosedAt = clock.GetUtcNow();
        }

        RecordWorkOrderAudit(actor, workOrder, "Transition", before);
        await SaveChangesAsync();
        await transaction.CommitAsync();
        return workOrder;
    }

    public async Task<WorkOrder> NoteAsync(Actor actor, Guid id, AppendWorkOrderNote input)
    {
        ValidateText(input.Text, 8000, true);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var workOrder = await GetForUpdateAsync(actor, id, input.Version);
        if (workOrder.Status is "Closed" or "Cancelled")
        {
            throw new DirectoryException(400, "Terminal work orders cannot be modified.");
        }

        var before = JsonSerializer.Serialize(workOrder);
        workOrder.Version++;
        var note = new WorkOrderNote
        {
            WorkOrderId = id,
            Text = input.Text.Trim(),
            ActorUserId = actor.UserId,
            OccurredAt = clock.GetUtcNow()
        };
        db.WorkOrderNotes.Add(note);
        RecordWorkOrderAudit(actor, workOrder, "AppendNote", before);
        db.AuditLogs.Add(new AuditLog
        {
            PlantId = workOrder.PlantId,
            ResourceId = note.Id,
            ResourceType = "WorkOrderNote",
            Action = "Create",
            ActorUserId = actor.UserId,
            After = JsonSerializer.Serialize(note),
            CorrelationId = actor.CorrelationId,
            OccurredAt = clock.GetUtcNow()
        });
        await SaveChangesAsync();
        await transaction.CommitAsync();
        return workOrder;
    }

    private static string TransitionNoteKind(string currentStatus, string nextStatus)
    {
        if (nextStatus == "Cancelled")
        {
            return "Cancellation";
        }

        return currentStatus == "Completed" && nextStatus == "InProgress" ? "Rework" : "Transition";
    }
}
