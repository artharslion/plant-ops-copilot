using System.Text.Json.Serialization;
using PlantOps.Application.Assets;

namespace PlantOps.Application.AI;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkOrderDraft(string Title, string Description, string Priority = "Medium", string? AssignedToUserId = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EditActionProposal(long Version, WorkOrderDraft Draft);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkOrderNoteDraft(string Text);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateNoteProposal(Guid WorkOrderId, long WorkOrderVersion);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EditNoteProposal(long Version, WorkOrderNoteDraft Draft);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ConfirmActionProposal(long Version, string PayloadHash, string IdempotencyKey);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RejectActionProposal(long Version);

public sealed record ActionProposalDto(Guid Id, Guid AiRunId, Guid PlantId, Guid AssetId, Guid AlarmId,
    string AssetName, string Status, long Version, string PayloadHash, DateTimeOffset ExpiresAt,
    WorkOrderDraft? Draft, InvestigationRunDto? Investigation, Guid? ResultEntityId,
    DateTimeOffset? ExecutedAt, string? ApprovalCorrelationId, string? UnavailableReason,
    string ActionType, Guid? TargetWorkOrderId, long? TargetWorkOrderVersion, string? TargetWorkOrderNumber,
    string? TargetWorkOrderTitle, WorkOrderNoteDraft? NoteDraft);

public interface IActionProposalService
{
    Task<ActionProposalDto> CreateAsync(Actor actor, Guid runId, CancellationToken cancellationToken = default);

    Task<ActionProposalDto> CreateNoteAsync(Actor actor, Guid runId, CreateNoteProposal input, CancellationToken cancellationToken = default);

    Task<ActionProposalDto> EditNoteAsync(Actor actor, Guid id, EditNoteProposal input, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ActionProposalDto>> ListAsync(Actor actor, Guid runId, CancellationToken cancellationToken = default);

    Task<ActionProposalDto> GetAsync(Actor actor, Guid id, CancellationToken cancellationToken = default);

    Task<ActionProposalDto> EditAsync(Actor actor, Guid id, EditActionProposal input, CancellationToken cancellationToken = default);

    Task<ActionProposalDto> RejectAsync(Actor actor, Guid id, RejectActionProposal input, CancellationToken cancellationToken = default);

    Task<ActionProposalDto> ExecuteAsync(Actor actor, Guid id, ConfirmActionProposal input, CancellationToken cancellationToken = default);
}
