namespace PlantOps.Application.AI;

// Metadata may be returned with an authorized run. Response bodies belong only in
// the private diagnostic store, never in RunRecordJson, exceptions or console logs.
public sealed record InvestigationResponseCapture(Guid RunId, Guid LeaseId, int Round, int Attempt,
    string RequestSha256, DateTimeOffset ReceivedAt, LlmCompletion Completion);

public sealed record InvestigationResponseReceipt(string Status, string? CaptureId = null);

public interface IInvestigationResponseStore
{
    Task<InvestigationResponseReceipt> SaveAsync(InvestigationResponseCapture capture, CancellationToken cancellationToken);

    Task PruneAsync(CancellationToken cancellationToken);
}

public sealed record LlmValidationDetails(string Branch, string Path, string? ItemIdSha256 = null,
    string[]? EvidenceRefs = null, string[]? EvidenceKinds = null, string[]? EvidenceAvailability = null,
    string[]? RejectedRefSha256 = null);
