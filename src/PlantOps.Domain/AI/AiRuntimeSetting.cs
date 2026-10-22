namespace PlantOps.Domain.AI;

public sealed class AiRuntimeSetting
{
    public int Id { get; set; } = 1;

    public string Provider { get; set; } = "Fake";

    public long Version { get; set; } = 1;
}
