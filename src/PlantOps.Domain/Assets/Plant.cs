namespace PlantOps.Domain.Assets;

public sealed class Plant
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string DisplayTimeZone { get; set; } = "UTC";
    public bool IsArchived { get; set; }
    public long Version { get; set; } = 1;
}
