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

public sealed class WorkOrderService(PlantOpsDbContext db, IAssetDirectory directory, UserManager<PlantOpsUser> users, TimeProvider clock) : IWorkOrderService
{
    private async Task Writer(Actor actor)
    {
        var u = await users.FindByIdAsync(actor.UserId);
        if (u is not { IsEnabled: true }) throw new DirectoryException(401, "Session unavailable.");
        if (!(await users.GetRolesAsync(u)).Intersect(new[] { "Admin", "MaintenanceEngineer" }).Any()) throw new DirectoryException(403, "Maintenance role required.");
    }
    public async Task<IReadOnlyList<MaintenanceUser>> AssigneesAsync(Actor actor, Guid plantId)
    {
        await directory.GetAsync(actor, "plants", plantId);
        var ids = db.UserPlantAccess.Where(x => x.PlantId == plantId).Select(x => x.UserId);
        var roles = db.Roles.Where(x => x.Name == "Admin" || x.Name == "MaintenanceEngineer").Select(x => x.Id);
        var eligible = db.UserRoles.Where(x => roles.Contains(x.RoleId)).Select(x => x.UserId);
        return await db.Users.AsNoTracking().Where(x => x.IsEnabled && ids.Contains(x.Id) && eligible.Contains(x.Id))
            .OrderBy(x => x.DisplayName).ThenBy(x => x.Id).Select(x => new MaintenanceUser(x.Id, x.DisplayName)).ToListAsync();
    }
    public async Task<WorkOrder> GetAsync(Actor actor, Guid id)
    {
        var w = await db.WorkOrders.SingleOrDefaultAsync(x => x.Id == id) ?? throw new DirectoryException(404, "Work order not found.");
        await directory.GetAsync(actor, "assets", w.AssetId); return w;
    }
    public async Task<IReadOnlyList<WorkOrderNote>> NotesAsync(Actor actor, Guid id)
    {
        await GetAsync(actor, id);
        return await db.WorkOrderNotes.AsNoTracking().Where(x => x.WorkOrderId == id).OrderBy(x => x.OccurredAt).ThenBy(x => x.Id).ToListAsync();
    }
    public async Task<WorkOrderPage> ListAsync(Actor actor, Guid assetId, bool history, int limit, string? cursor)
    {
        await directory.GetAsync(actor, "assets", assetId);
        if (limit is < 1 or > 200 || cursor?.Length > 128) throw new DirectoryException(400, "Invalid page size or cursor.");
        var q = db.WorkOrders.AsNoTracking().Where(x => x.AssetId == assetId && (!history || x.Status == "Completed" || x.Status == "Closed"));
        if (cursor != null)
        {
            DateTimeOffset at; Guid id;
            try { var p = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split(':'); if (p.Length != 2) throw new FormatException(); at = new(long.Parse(p[0]), TimeSpan.Zero); id = Guid.ParseExact(p[1], "N"); }
            catch (Exception e) when (e is FormatException or OverflowException or ArgumentOutOfRangeException) { throw new DirectoryException(400, "Invalid cursor."); }
            q = q.Where(x => x.CreatedAt < at || x.CreatedAt == at && x.Id.CompareTo(id) < 0);
        }
        var rows = await q.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Take(limit + 1).ToListAsync(); var page = rows.Take(limit).ToList();
        return new(page, rows.Count > limit ? Convert.ToBase64String(Encoding.UTF8.GetBytes($"{page[^1].CreatedAt.UtcTicks}:{page[^1].Id:N}")) : null);
    }
    private static void Text(string? value, int max, bool required = false)
    { if (value?.Length > max || required && string.IsNullOrWhiteSpace(value)) throw new DirectoryException(400, "Missing or oversized text."); }
    private async Task Lock(Guid plant) => await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({plant.ToString()}, 0))");
    private void Audit(Actor actor, WorkOrder w, string action, string? before)
    { db.AuditLogs.Add(new AuditLog { PlantId = w.PlantId, ResourceId = w.Id, ResourceType = "WorkOrder", Action = action, ActorUserId = actor.UserId, Before = before, After = JsonSerializer.Serialize(w), CorrelationId = actor.CorrelationId, OccurredAt = clock.GetUtcNow() }); }
    private async Task Save()
    { try { await db.SaveChangesAsync(); } catch (DbUpdateConcurrencyException) { throw new DirectoryException(409, "Work order version conflict."); } }
    public async Task<WorkOrder> CreateAsync(Actor actor, CreateWorkOrder input)
    {
        await Writer(actor); var asset = await directory.GetAsync(actor, "assets", input.AssetId);
        Text(input.Title, 200, true); Text(input.Description, 8000);
        if (input.Priority is not ("Low" or "Medium" or "High")) throw new DirectoryException(400, "Invalid priority.");
        await using var tx = await db.Database.BeginTransactionAsync(); await Lock(asset.PlantId);
        await Writer(actor); asset = await directory.GetAsync(actor, "assets", input.AssetId);
        if (asset.IsArchived || (await directory.GetAsync(actor, "plants", asset.PlantId)).IsArchived) throw new DirectoryException(400, "Archived resources cannot receive new work orders.");
        if (input.AlarmId.HasValue && !await db.Alarms.AnyAsync(x => x.Id == input.AlarmId && x.PlantId == asset.PlantId && x.AssetId == asset.Id)) throw new DirectoryException(404, "Associated alarm not found on this asset.");
        if (input.AssignedToUserId != null && !(await AssigneesAsync(actor, asset.PlantId)).Any(x => x.Id == input.AssignedToUserId)) throw new DirectoryException(400, "Assignee requires enabled maintenance access to this plant.");
        var w = new WorkOrder { PlantId = asset.PlantId, AssetId = asset.Id, AlarmId = input.AlarmId, Title = input.Title.Trim(), Description = input.Description,
            Priority = input.Priority, AssignedToUserId = input.AssignedToUserId, CreatedByUserId = actor.UserId, CreatedAt = clock.GetUtcNow() };
        w.Number = $"WO-{w.Id:N}"; db.WorkOrders.Add(w); Audit(actor, w, "Create", null); await Save(); await tx.CommitAsync(); return w;
    }
    private async Task<WorkOrder> ForWrite(Actor actor, Guid id, long version)
    {
        await Writer(actor); var w = await GetAsync(actor, id); await Lock(w.PlantId);
        await Writer(actor); await directory.GetAsync(actor, "assets", w.AssetId); await db.Entry(w).ReloadAsync();
        if (w.Version != version) throw new DirectoryException(409, "Work order version conflict."); return w;
    }
    public async Task<WorkOrder> TransitionAsync(Actor actor, Guid id, TransitionWorkOrder input)
    {
        await using var tx = await db.Database.BeginTransactionAsync(); var w = await ForWrite(actor, id, input.Version);
        Text(input.Reason, 8000); Text(input.Resolution, 8000);
        var result = input.Status == "Completed" ? input.Resolution : w.Resolution;
        var error = WorkOrderState.Validate(w.Status, input.Status, input.Reason, result); if (error != null) throw new DirectoryException(400, error);
        var before = JsonSerializer.Serialize(w);
        if (input.Reason != null) db.WorkOrderNotes.Add(new WorkOrderNote { WorkOrderId = id, Text = input.Reason, Kind = input.Status == "Cancelled" ? "Cancellation" : w.Status == "Completed" && input.Status == "InProgress" ? "Rework" : "Transition", ActorUserId = actor.UserId, OccurredAt = clock.GetUtcNow() });
        w.Status = input.Status; w.Version++;
        if (input.Status == "Completed") { w.CompletedAt = clock.GetUtcNow(); w.Resolution = input.Resolution!.Trim(); }
        if (input.Status == "InProgress") { w.CompletedAt = null; w.Resolution = null; }
        if (input.Status == "Closed") w.ClosedAt = clock.GetUtcNow();
        Audit(actor, w, "Transition", before); await Save(); await tx.CommitAsync(); return w;
    }
    public async Task<WorkOrder> NoteAsync(Actor actor, Guid id, AppendWorkOrderNote input)
    {
        Text(input.Text, 8000, true); await using var tx = await db.Database.BeginTransactionAsync(); var w = await ForWrite(actor, id, input.Version);
        if (w.Status is "Closed" or "Cancelled") throw new DirectoryException(400, "Terminal work orders cannot be modified.");
        var before = JsonSerializer.Serialize(w); w.Version++;
        var note = new WorkOrderNote { WorkOrderId = id, Text = input.Text.Trim(), ActorUserId = actor.UserId, OccurredAt = clock.GetUtcNow() }; db.WorkOrderNotes.Add(note);
        Audit(actor, w, "AppendNote", before);
        db.AuditLogs.Add(new AuditLog { PlantId = w.PlantId, ResourceId = note.Id, ResourceType = "WorkOrderNote", Action = "Create", ActorUserId = actor.UserId, After = JsonSerializer.Serialize(note), CorrelationId = actor.CorrelationId, OccurredAt = clock.GetUtcNow() });
        await Save(); await tx.CommitAsync(); return w;
    }
}
