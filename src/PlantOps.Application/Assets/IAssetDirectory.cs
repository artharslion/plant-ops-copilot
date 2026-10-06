namespace PlantOps.Application.Assets;

public sealed record Actor(string UserId, string CorrelationId);

public sealed record DirectoryRecord(Guid Id, Guid PlantId, string Code, string Name, long Version,
    bool IsArchived, string? Description = null, Guid? ParentLocationId = null, int SortOrder = 0,
    string DisplayTimeZone = "UTC", Guid? LocationId = null, string AssetType = "Pump",
    string LifecycleStatus = "Active", string Criticality = "Medium", string? Manufacturer = null,
    string? Model = null, string? SerialNumber = null, DateTimeOffset? InstalledAt = null);

public sealed record DirectoryInput(string Code, string Name, long Version = 0, bool IsArchived = false,
    string? Description = null, Guid? ParentLocationId = null, int SortOrder = 0, string DisplayTimeZone = "UTC",
    Guid? LocationId = null, string AssetType = "Pump", string LifecycleStatus = "Active",
    string Criticality = "Medium", string? Manufacturer = null, string? Model = null,
    string? SerialNumber = null, DateTimeOffset? InstalledAt = null);

public sealed record DirectoryPage(IReadOnlyList<DirectoryRecord> Items, string? NextCursor);

public sealed class DirectoryException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

public interface IAssetDirectory
{
    Task<DirectoryPage> ListAsync(Actor actor, string kind, Guid? plantId, Guid? locationId, int limit, string? cursor);

    Task<DirectoryRecord> GetAsync(Actor actor, string kind, Guid id);

    Task<DirectoryRecord> SaveAsync(Actor actor, string kind, Guid? id, Guid? plantId, DirectoryInput input);
}
