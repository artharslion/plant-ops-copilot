namespace PlantOps.Domain.Assets;

public sealed class Asset
{
    public Guid Id
    {
        get; set;
    }

    public Guid PlantId
    {
        get; set;
    }

    public Guid LocationId
    {
        get; set;
    }

    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string AssetType { get; set; } = string.Empty;

    public string LifecycleStatus { get; set; } = "Active";

    public string? Manufacturer
    {
        get; set;
    }

    public string? Model
    {
        get; set;
    }

    public string? SerialNumber
    {
        get; set;
    }

    public string Criticality { get; set; } = "Medium";

    public DateTimeOffset? InstalledAt
    {
        get; set;
    }

    public bool IsArchived
    {
        get; set;
    }

    public long Version { get; set; } = 1;
}
