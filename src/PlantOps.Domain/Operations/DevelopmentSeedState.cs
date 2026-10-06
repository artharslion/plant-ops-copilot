namespace PlantOps.Domain.Operations;

public sealed class DevelopmentSeedState
{
    public string Name { get; set; } = "";

    public DateTimeOffset SeededAt
    {
        get; set;
    }
}
