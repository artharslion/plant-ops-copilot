namespace PlantOps.Domain.AI;

public sealed class AiRun
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid PlantId
    {
        get; set;
    }

    public Guid AssetId
    {
        get; set;
    }

    public Guid AlarmId
    {
        get; set;
    }

    public string ActorUserId { get; set; } = "";

    public string CorrelationId { get; set; } = "";

    public string Status { get; set; } = "Pending";

    public DateTimeOffset CreatedAt
    {
        get; set;
    }

    public DateTimeOffset? StartedAt
    {
        get; set;
    }

    public DateTimeOffset? CompletedAt
    {
        get; set;
    }

    public DateTimeOffset? LeaseUntil
    {
        get; set;
    }

    public Guid? LeaseId
    {
        get; set;
    }

    public int Attempts
    {
        get; set;
    }

    public string Provider { get; set; } = "Fake";

    public string Model { get; set; } = "";

    public bool Simulated
    {
        get; set;
    }

    public string PromptVersion { get; set; } = "investigation-extractive-v1";

    public string? FailureCode
    {
        get; set;
    }

    public string? ResultJson
    {
        get; set;
    }

    public string EvidenceJson { get; set; } = "[]";

    public string WarningsJson { get; set; } = "[]";

    public int ModelRounds
    {
        get; set;
    }

    public int ToolCalls
    {
        get; set;
    }

    public int InputTokens
    {
        get; set;
    }

    public int OutputTokens
    {
        get; set;
    }
}
