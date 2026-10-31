using System.Text.Json;
using PlantOps.Application.Assets;

namespace PlantOps.Application.AI;

public sealed record InvestigationEvidence(string Id, string Kind, string Title, string[] Facts, string Sha256,
    DateTimeOffset RetrievedAt, Guid ResourceId, Guid? VersionId = null, Guid? ChunkId = null,
    InvestigationPointSnapshot? PointStatus = null, InvestigationDerivedTrend? DerivedTrend = null,
    InvestigationEvidenceState? State = null);

// Availability describes the queried evidence, not measurement trust or equipment health.
// Partial means a bounded excerpt; Empty means a successful query returned no records.
public sealed record InvestigationEvidenceState(string Availability, string? CurrentTrust = null, string? CurrentAnomaly = null);

public sealed record InvestigationToolResult(IReadOnlyList<InvestigationEvidence> Evidence, IReadOnlyList<string> Warnings);

public sealed record InvestigationExecutionResult(string ResultJson, string Model, bool Simulated);

public interface IInvestigationEngine
{
    Task<InvestigationExecutionResult> ExecuteAsync(IReadOnlyList<InvestigationEvidence> evidence,
        Func<string, JsonElement, CancellationToken, Task<InvestigationToolResult>> readTool,
        Func<CancellationToken, Task> authorize, LlmBudget budget, CancellationToken cancellationToken, string language = "zh-CN");
}

public sealed record InvestigationRunDto(Guid Id, Guid AlarmId, Guid AssetId, string Status, DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, string Provider, string Model, bool Simulated,
    string? FailureCode, JsonElement? Result, IReadOnlyList<InvestigationEvidence> Evidence, IReadOnlyList<string> Warnings,
    int ModelRounds, int ToolCalls, int InputTokens, int OutputTokens, JsonElement? RunRecord = null,
    InvestigationAssessment? Assessment = null, string Language = "zh-CN");

public interface IInvestigationService
{
    Task<InvestigationRunDto> CreateAsync(Actor actor, Guid alarmId, CancellationToken cancellationToken = default, string language = "zh-CN");

    Task<IReadOnlyList<InvestigationRunDto>> ListAsync(Actor actor, Guid alarmId, CancellationToken cancellationToken = default);

    Task<InvestigationRunDto> GetAsync(Actor actor, Guid id, CancellationToken cancellationToken = default);

    Task<InvestigationRunDto> CancelAsync(Actor actor, Guid id, CancellationToken cancellationToken = default);

    Task<InvestigationEvidence> ExpandAsync(Actor actor, Guid id, string evidenceId, CancellationToken cancellationToken = default);

    Task<bool> ProcessNextAsync(CancellationToken cancellationToken = default);
}
