namespace PlantOps.Domain.AI;

public sealed class ActionProposal
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public int SchemaVersion { get; set; } = 1;

    public Guid AiRunId
    {
        get; set;
    }

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

    public string OwnerUserId { get; set; } = "";

    public string ActionType { get; set; } = "CreateWorkOrder";

    public Guid? TargetWorkOrderId
    {
        get; set;
    }

    public long? TargetWorkOrderVersion
    {
        get; set;
    }

    public string DraftOrigin { get; set; } = "InvestigationTemplate";

    public string Status { get; set; } = "Pending";

    public string PayloadJson { get; set; } = "{}";

    public string PayloadHash { get; set; } = "";

    public string InvestigationHash { get; set; } = "";

    public long AssetVersion
    {
        get; set;
    }

    public long AlarmVersion
    {
        get; set;
    }

    public long Version { get; set; } = 1;

    public DateTimeOffset CreatedAt
    {
        get; set;
    }

    public DateTimeOffset ExpiresAt
    {
        get; set;
    }

    public Guid? ResultEntityId
    {
        get; set;
    }

    public DateTimeOffset? ExecutedAt
    {
        get; set;
    }

    public string? ApprovalCorrelationId
    {
        get; set;
    }

    public string? ExecutionKey
    {
        get; set;
    }
}
