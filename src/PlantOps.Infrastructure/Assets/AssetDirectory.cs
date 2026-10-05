using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PlantOps.Application.Assets;
using PlantOps.Domain.Assets;
using PlantOps.Domain.Identity;
using PlantOps.Infrastructure.Identity;
using PlantOps.Infrastructure.Persistence;

namespace PlantOps.Infrastructure.Assets;

public sealed class AssetDirectory(PlantOpsDbContext db, UserManager<PlantOpsUser> users) : IAssetDirectory
{
    private static readonly string[] ReaderRoles = ["Viewer", "Operator", "MaintenanceEngineer", "Admin"];
    private async Task RequireRole(Actor actor, bool write)
    {
        var user = await users.FindByIdAsync(actor.UserId);
        if (user is null || !user.IsEnabled) throw new DirectoryException(401, "Session unavailable.");
        var roles = await users.GetRolesAsync(user);
        if (write ? !roles.Contains("Admin") : !roles.Intersect(ReaderRoles).Any())
            throw new DirectoryException(403, "Role does not allow this action.");
    }
    private IQueryable<Guid> Scope(Actor actor) => db.UserPlantAccess.Where(x => x.UserId == actor.UserId).Select(x => x.PlantId);
    private async Task RequireScope(Actor actor, Guid plantId)
    {
        if (!await Scope(actor).ContainsAsync(plantId)) throw new DirectoryException(404, "Resource not found.");
    }

    public async Task<DirectoryPage> ListAsync(Actor actor, string kind, Guid? plantId, Guid? locationId, int limit, string? cursor)
    {
        await RequireRole(actor, false);
        if (limit is < 1 or > 200 || cursor?.Length > 64) throw new DirectoryException(400, "Invalid page size or cursor.");
        if (plantId.HasValue) await RequireScope(actor, plantId.Value);
        if (kind != "plants" && !plantId.HasValue) throw new DirectoryException(400, "plantId is required.");
        if (locationId.HasValue && !await db.Locations.AnyAsync(x => x.Id == locationId && x.PlantId == plantId))
            throw new DirectoryException(404, "Location not found.");
        List<DirectoryRecord> rows;
        if (kind == "plants")
        {
            var query = db.Plants.AsNoTracking().Where(x => Scope(actor).Contains(x.Id));
            if (cursor != null) query = query.Where(x => string.Compare(x.Code, cursor) > 0);
            rows = (await query.OrderBy(x => x.Code).Take(limit + 1).ToListAsync()).Select(Map).ToList();
        }
        else if (kind == "locations")
        {
            var query = db.Locations.AsNoTracking().Where(x => x.PlantId == plantId);
            if (cursor != null) query = query.Where(x => string.Compare(x.Code, cursor) > 0);
            rows = (await query.OrderBy(x => x.Code).Take(limit + 1).ToListAsync()).Select(Map).ToList();
        }
        else
        {
            var query = db.Assets.AsNoTracking().Where(x => x.PlantId == plantId && (!locationId.HasValue || x.LocationId == locationId));
            if (cursor != null) query = query.Where(x => string.Compare(x.Code, cursor) > 0);
            rows = (await query.OrderBy(x => x.Code).Take(limit + 1).ToListAsync()).Select(Map).ToList();
        }
        return new(rows.Take(limit).ToList(), rows.Count > limit ? rows[limit - 1].Code : null);
    }

    private async Task<object> Load(string kind, Guid id) => kind switch
    {
        "plants" => (object?)await db.Plants.FindAsync(id) ?? throw new DirectoryException(404, "Resource not found."),
        "locations" => (object?)await db.Locations.FindAsync(id) ?? throw new DirectoryException(404, "Resource not found."),
        "assets" => (object?)await db.Assets.FindAsync(id) ?? throw new DirectoryException(404, "Resource not found."),
        _ => throw new DirectoryException(404, "Resource not found.")
    };
    private static DirectoryRecord Map(object entity) => entity switch
    {
        Plant x => new(x.Id, x.Id, x.Code, x.Name, x.Version, x.IsArchived, x.Description, DisplayTimeZone: x.DisplayTimeZone),
        Location x => new(x.Id, x.PlantId, x.Code, x.Name, x.Version, x.IsArchived, x.Description, x.ParentLocationId, x.SortOrder),
        Asset x => new(x.Id, x.PlantId, x.Code, x.Name, x.Version, x.IsArchived, LocationId: x.LocationId,
            AssetType: x.AssetType, LifecycleStatus: x.LifecycleStatus, Criticality: x.Criticality,
            Manufacturer: x.Manufacturer, Model: x.Model, SerialNumber: x.SerialNumber, InstalledAt: x.InstalledAt),
        _ => throw new InvalidOperationException()
    };
    public async Task<DirectoryRecord> GetAsync(Actor actor, string kind, Guid id)
    {
        await RequireRole(actor, false);
        var result = Map(await Load(kind, id));
        await RequireScope(actor, result.PlantId);
        return result;
    }

    private static void Validate(DirectoryInput input, string kind)
    {
        if (string.IsNullOrWhiteSpace(input.Code) || input.Code.Length > 64 || input.Code != input.Code.Trim()
            || string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 200 || input.Description?.Length > 8000)
            throw new DirectoryException(400, "Code (1–64), name (1–200) and description (up to 8000) are required within limits.");
        if (kind == "plants")
        {
            if (string.IsNullOrWhiteSpace(input.DisplayTimeZone)) throw new DirectoryException(400, "Display time zone required.");
            try { TimeZoneInfo.FindSystemTimeZoneById(input.DisplayTimeZone); }
            catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
            { throw new DirectoryException(400, "Invalid display time zone."); }
            if (input.DisplayTimeZone.Length > 100) throw new DirectoryException(400, "Time zone is too long.");
        }
        if (kind == "assets" && (string.IsNullOrWhiteSpace(input.AssetType) || input.AssetType.Length > 64
            || !new[] { "Active", "OutOfService", "Retired" }.Contains(input.LifecycleStatus)
            || !new[] { "Low", "Medium", "High" }.Contains(input.Criticality)
            || input.Manufacturer?.Length > 200 || input.Model?.Length > 200 || input.SerialNumber?.Length > 200))
            throw new DirectoryException(400, "Invalid asset fields.");
    }

    public async Task<DirectoryRecord> SaveAsync(Actor actor, string kind, Guid? id, Guid? plantId, DirectoryInput input)
    {
        await RequireRole(actor, true);
        object? entity = id.HasValue ? await Load(kind, id.Value) : null;
        var before = entity == null ? null : Map(entity);
        var scopeId = before?.PlantId ?? (kind == "plants" ? Guid.NewGuid() : plantId ?? throw new DirectoryException(400, "plantId required."));
        if (before != null || kind != "plants") await RequireScope(actor, scopeId);
        Validate(input, kind);
        await using var transaction = await db.Database.BeginTransactionAsync();
        // All directory writes in a plant share this lock, including moves and archive checks.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({scopeId.ToString()}, 0))");
        await RequireRole(actor, true);
        if (before != null || kind != "plants") await RequireScope(actor, scopeId);
        if (entity != null)
        {
            await db.Entry(entity).ReloadAsync();
            before = Map(entity);
            if (input.Version != before.Version) throw new DirectoryException(409, "Version conflict. Refresh before editing.");
            if (before.IsArchived && !input.IsArchived) throw new DirectoryException(400, "Archived resources cannot be restored in this stage.");
        }
        if (kind != "plants" && !await db.Plants.AnyAsync(x => x.Id == scopeId && !x.IsArchived))
            throw new DirectoryException(400, "Plant is archived or missing.");
        if (kind == "plants" && input.IsArchived && await db.Assets.AnyAsync(x => x.PlantId == scopeId && !x.IsArchived))
            throw new DirectoryException(400, "Archive active assets before archiving the plant.");
        if (kind == "plants" && input.IsArchived && await db.DataSources.AnyAsync(x => x.PlantId == scopeId && x.Enabled))
            throw new DirectoryException(400, "Disable data sources before archiving the plant.");
        if (kind == "locations")
        {
            if (input.IsArchived && (await db.Assets.AnyAsync(x => x.LocationId == id && !x.IsArchived)
                || await db.Locations.AnyAsync(x => x.ParentLocationId == id && !x.IsArchived)))
                throw new DirectoryException(400, "Archive or move active children and assets first.");
            var parentId = input.ParentLocationId;
            var visited = new HashSet<Guid>();
            while (parentId.HasValue)
            {
                if (parentId == id || !visited.Add(parentId.Value)) throw new DirectoryException(400, "Location cycle is forbidden.");
                var parent = await db.Locations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == parentId && x.PlantId == scopeId && !x.IsArchived)
                    ?? throw new DirectoryException(400, "Parent must be an active location in the same plant.");
                parentId = parent.ParentLocationId;
            }
        }
        if (kind == "assets" && !await db.Locations.AnyAsync(x => x.Id == input.LocationId && x.PlantId == scopeId && !x.IsArchived))
            throw new DirectoryException(400, "Asset location must be active and in the same plant.");
        var version = (before?.Version ?? 0) + 1;
        var entityId = id ?? (kind == "plants" ? scopeId : Guid.NewGuid());
        switch (kind)
        {
            case "plants":
                var p = entity as Plant ?? new Plant { Id = entityId };
                p.Code = input.Code; p.Name = input.Name; p.Description = input.Description;
                p.DisplayTimeZone = input.DisplayTimeZone; p.IsArchived = input.IsArchived; p.Version = version;
                if (entity == null)
                {
                    db.Plants.Add(p);
                    db.UserPlantAccess.Add(new UserPlantAccess { UserId = actor.UserId, PlantId = p.Id,
                        GrantedAt = DateTimeOffset.UtcNow, GrantedByUserId = actor.UserId });
                }
                entity = p;
                break;
            case "locations":
                var l = entity as Location ?? new Location { Id = entityId, PlantId = scopeId };
                l.Code = input.Code; l.Name = input.Name; l.Description = input.Description;
                l.ParentLocationId = input.ParentLocationId; l.SortOrder = input.SortOrder;
                l.IsArchived = input.IsArchived; l.Version = version;
                if (entity == null) db.Locations.Add(l);
                entity = l;
                break;
            case "assets":
                var a = entity as Asset ?? new Asset { Id = entityId, PlantId = scopeId };
                a.Code = input.Code; a.Name = input.Name; a.LocationId = input.LocationId!.Value;
                a.AssetType = input.AssetType; a.LifecycleStatus = input.LifecycleStatus; a.Criticality = input.Criticality;
                a.Manufacturer = input.Manufacturer; a.Model = input.Model; a.SerialNumber = input.SerialNumber;
                a.InstalledAt = input.InstalledAt?.ToUniversalTime(); a.IsArchived = input.IsArchived; a.Version = version;
                if (entity == null) db.Assets.Add(a);
                entity = a;
                break;
            default: throw new DirectoryException(404, "Unknown resource.");
        }
        var after = Map(entity);
        if (kind == "assets" && input.IsArchived && before is { IsArchived: false })
        {
            var points = await db.MeasurementPoints.Where(x => x.AssetId == entityId && x.Enabled).ToListAsync();
            var mappings = await db.SourceMappings.Where(x => db.MeasurementPoints.Any(p => p.Id == x.PointId && p.AssetId == entityId) && x.Enabled).ToListAsync();
            foreach (var point in points)
            {
                var prior = JsonSerializer.Serialize(point); point.Enabled = false; point.Version++;
                db.AuditLogs.Add(new AuditLog { ActorUserId = actor.UserId, PlantId = scopeId, ResourceId = point.Id,
                    ResourceType = "MeasurementPoint", Action = "Disable", Before = prior, After = JsonSerializer.Serialize(point),
                    OccurredAt = DateTimeOffset.UtcNow, CorrelationId = actor.CorrelationId });
            }
            foreach (var mapping in mappings)
            {
                var prior = JsonSerializer.Serialize(mapping); mapping.Enabled = false; mapping.Version++;
                db.AuditLogs.Add(new AuditLog { ActorUserId = actor.UserId, PlantId = scopeId, ResourceId = mapping.Id,
                    ResourceType = "SourceMapping", Action = "Disable", Before = prior, After = JsonSerializer.Serialize(mapping),
                    OccurredAt = DateTimeOffset.UtcNow, CorrelationId = actor.CorrelationId });
            }
        }
        db.AuditLogs.Add(new AuditLog { ActorUserId = actor.UserId, PlantId = scopeId, ResourceId = entityId,
            ResourceType = kind, Action = before == null ? "Create" : input.IsArchived && !before.IsArchived ? "Archive" : "Update",
            Before = before == null ? null : JsonSerializer.Serialize(before), After = JsonSerializer.Serialize(after),
            OccurredAt = DateTimeOffset.UtcNow, CorrelationId = actor.CorrelationId });
        try
        {
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch (DbUpdateConcurrencyException) { throw new DirectoryException(409, "Version conflict. Refresh before editing."); }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: "23505" })
        { throw new DirectoryException(400, "Code already exists in this scope."); }
        return after;
    }
}
