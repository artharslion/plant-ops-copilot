namespace PlantOps.Domain.Operations;
public sealed class OutboxEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PlantId { get; set; }
    public Guid EntityId { get; set; }
    public string EventType { get; set; } = "";
    public long EntityVersion { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset AvailableAt { get; set; }
    public DateTimeOffset? DispatchedAt { get; set; }
    public int Attempts { get; set; }
}
