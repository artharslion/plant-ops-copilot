using PlantOps.Application.Assets;

namespace PlantOps.Application.Operations;

public sealed record OverviewCounts(int ActiveAlarms, int UnacknowledgedAlarms, int OpenWorkOrders, int DataIssues);

public sealed record OverviewAlarm(Guid Id, Guid AssetId, string AssetCode, string AssetName, string RuleName,
    string Severity, DateTimeOffset StartedAt, DateTimeOffset? AcknowledgedAt);

public sealed record OverviewWorkOrder(Guid Id, Guid AssetId, string AssetCode, string Number, string Title,
    string Priority, string Status, string? AssignedTo, DateTimeOffset CreatedAt);

public sealed record OverviewDataIssue(Guid PointId, Guid AssetId, string AssetCode, string AssetName,
    string PointName, string Reason, DateTimeOffset? LastReceivedAt);

public sealed record PlantOverview(Guid PlantId, DateTimeOffset CheckedAt, OverviewCounts Counts,
    IReadOnlyList<OverviewAlarm> Alarms, int MatchingAlarms, IReadOnlyList<OverviewWorkOrder> WorkOrders,
    int MatchingWorkOrders, IReadOnlyList<OverviewDataIssue> DataIssues);

public interface IOverviewService
{
    Task<PlantOverview> GetAsync(Actor actor, Guid plantId, bool unacknowledgedOnly, bool mine,
        string? workOrderStatus, int limit);
}
