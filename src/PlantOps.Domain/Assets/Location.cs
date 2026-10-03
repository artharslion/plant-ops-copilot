namespace PlantOps.Domain.Assets;

public sealed class Location
{
    public Guid Id { get; set; }
    public Guid PlantId { get; set; }
    public Guid? ParentLocationId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsArchived { get; set; }
    public long Version { get; set; } = 1;
}
