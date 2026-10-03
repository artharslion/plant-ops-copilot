namespace PlantOps.Domain.Assets;

public sealed class Asset
{
    public Guid Id { get; set; }
    public Guid PlantId { get; set; }
    public Guid LocationId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string AssetType { get; set; } = string.Empty;
    public string LifecycleStatus { get; set; } = "Active";
    public long Version { get; set; } = 1;
}
