namespace PlantOps.Domain.Maintenance;

public sealed class WorkOrder
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

    public Guid? AlarmId
    {
        get; set;
    }

    public string Number { get; set; } = "";

    public string Title { get; set; } = "";

    public string Description { get; set; } = "";

    public string Priority { get; set; } = "Medium";

    public string Status { get; set; } = "Draft";

    public string? AssignedToUserId
    {
        get; set;
    }

    public string CreatedByUserId { get; set; } = "";

    public DateTimeOffset CreatedAt
    {
        get; set;
    }

    public DateTimeOffset? CompletedAt
    {
        get; set;
    }

    public DateTimeOffset? ClosedAt
    {
        get; set;
    }

    public string? Resolution
    {
        get; set;
    }

    public string Source { get; set; } = "Manual";

    public Guid? AiProposalId
    {
        get; set;
    }

    public long Version { get; set; } = 1;
}

public sealed class WorkOrderNote
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WorkOrderId
    {
        get; set;
    }

    public string Text { get; set; } = "";

    public string Kind { get; set; } = "Note";

    public string ActorUserId { get; set; } = "";

    public DateTimeOffset OccurredAt
    {
        get; set;
    }
}

public static class WorkOrderState
{
    public static string? Validate(string from, string to, string? reason, string? resolution)
    {
        var allowed = (from, to) is ("Draft", "Open") or ("Open", "InProgress") or ("InProgress", "Completed")
            or ("Completed", "Closed") or ("Completed", "InProgress")
            or ("Draft" or "Open" or "InProgress", "Cancelled");
        if (!allowed)
        {
            return "Invalid work order transition.";
        }

        if ((to == "Cancelled" || from == "Completed" && to == "InProgress") && string.IsNullOrWhiteSpace(reason))
        {
            return "Cancellation and rework require a reason.";
        }

        if (to is "Completed" or "Closed" && string.IsNullOrWhiteSpace(resolution))
        {
            return "A maintenance result is required.";
        }

        return null;
    }
}
