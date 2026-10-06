namespace PlantOps.Domain.Identity;

public sealed class AuditLog
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string ActorUserId { get; set; } = "";

    public string Action { get; set; } = "";

    public string ResourceType { get; set; } = "";

    public Guid ResourceId
    {
        get; set;
    }

    public Guid PlantId
    {
        get; set;
    }

    public string? Before
    {
        get; set;
    }

    public string After { get; set; } = "";

    public DateTimeOffset OccurredAt
    {
        get; set;
    }

    public string CorrelationId { get; set; } = "";
}
