using PlantOps.AI;
using PlantOps.Application.AI;
using Microsoft.Extensions.Options;
using System.Text.Json;
using Xunit;

namespace PlantOps.Tests;

// Historical v1/v2 validation and replay; not v3 semantic acceptance.
public sealed class LegacyInvestigationModelSchemaTests
{
    [Theory]
    [InlineData("{\"decision\":\"Ready\"}", true)]
    [InlineData("READY", false)]
    [InlineData("{\"decision\":\"ReadEvidence\"}", false)]
    [InlineData("{\"decision\":\"Ready\",\"extra\":true}", false)]
    [InlineData("{}", false)]
    [InlineData("null", false)]
    [InlineData(null, false)]
    public void EvidenceReadinessIsAStructuredDecision(string? content, bool ready)
    {
        Assert.Equal(ready, InvestigationToolDecision.IsReady(content));
    }

    [Fact]
    public void OmittedMetadataDoesNotCrowdOutARelevantDocumentSnippet()
    {
        var at = DateTimeOffset.UtcNow;
        var evidence = Enumerable.Range(0, 20).Select(i => new InvestigationEvidence("E" + i, "EventReadings",
            "Long raw evidence metadata", [new string('样', 1000)], "hash", at, Guid.NewGuid()))
            .Append(new("D1", "Document", "Procedure", ["只读核对仪表校验与现场比对记录。"], "hash", at, Guid.NewGuid())).ToArray();
        var packed = LegacyInvestigationEngine.ForModel(evidence, 512, omitEmptyMetadata: true);
        Assert.Contains("只读核对仪表校验与现场比对记录。", packed.Single(x => x.Id == "D1").Facts);
    }

    [Theory]
    [InlineData("Trusted")]
    [InlineData("Unverifiable")]
    public async Task EvidenceSelectionUsesASmallDecisionSchemaWithoutForcingATool(string trust)
    {
        var gateway = new CaptureGateway();
        var engine = new LegacyInvestigationEngine(gateway, Options.Create(new AiOptions { Provider = "OpenAiCompatible", ServerRenderedFacts = true }));
        var at = DateTimeOffset.UtcNow;
        var point = Guid.NewGuid();
        var evidence = new[]
        {
            new InvestigationEvidence("E1", "Alarm", "Alarm", ["Historical alarm."], "hash", at, Guid.NewGuid()),
            new InvestigationEvidence("E2", "PointStatus", "Point", ["Current point state."], "hash", at, point,
                PointStatus: new() { PointId = point, CurrentTrust = trust, CurrentAnomaly = trust == "Trusted" ? "Present" : "Insufficient" })
        };
        using var budget = new LlmBudget { EvidenceAsOf = at };
        await Assert.ThrowsAsync<LlmException>(() => engine.ExecuteAsync(evidence,
            (_, _, _) => Task.FromResult(new InvestigationToolResult([], [])), _ => Task.CompletedTask, budget, default));
        Assert.Null(gateway.Request!.RequiredTool);
        Assert.Equal("investigation_evidence_decision", gateway.Request.OutputSchema!.Name);
        Assert.True(gateway.Request.AllowParallelToolCalls);
        using var body = JsonDocument.Parse(ChatCompletionsProtocol.Serialize(gateway.Request, "test", null));
        Assert.True(body.RootElement.GetProperty("parallel_tool_calls").GetBoolean());
        Assert.Equal(5, gateway.Request.Tools!.Count);
        Assert.True(budget.Requests[0].GetProperty("selectingTools").GetBoolean());
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    public async Task ReadyDecisionStillNeedsAValidatedAnswerAndCannotSkipTrustedAnomalyReads(bool anomaly, int rounds)
    {
        var gateway = new ReadyGateway();
        var at = DateTimeOffset.UtcNow;
        var point = Guid.NewGuid();
        var evidence = new[]
        {
            new InvestigationEvidence("E1", "PointStatus", "Point", ["Current point state."], "hash", at, point,
                PointStatus: new() { PointId = point, CurrentTrust = anomaly ? "Trusted" : "Unverifiable", CurrentAnomaly = anomaly ? "Present" : "Insufficient" })
        };
        using var budget = new LlmBudget { EvidenceAsOf = at };
        var engine = new LegacyInvestigationEngine(gateway, Options.Create(new AiOptions { Provider = "OpenAiCompatible", ServerRenderedFacts = true }));
        await Assert.ThrowsAsync<LlmException>(() => engine.ExecuteAsync(evidence,
            (_, _, _) => throw new InvalidOperationException(), _ => Task.CompletedTask, budget, default));
        Assert.Equal(rounds, gateway.Requests.Count);
        Assert.Equal("investigation_evidence_decision", gateway.Requests[0].OutputSchema!.Name);
        if (!anomaly)
        {
            Assert.NotNull(gateway.Requests[1].OutputSchema);
        }
    }

    private sealed class ReadyGateway : ILlmGateway
    {
        public List<LlmRequest> Requests { get; } = [];

        public Task<LlmCompletion> CompleteAsync(LlmRequest request, LlmBudget budget, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            budget.BeginRound();
            return Requests.Count == 1 ? Task.FromResult(new LlmCompletion("""{"decision":"Ready"}""", [], "fake", null))
                : Task.FromException<LlmCompletion>(new LlmException(LlmFailure.InvalidRequest));
        }
    }

    [Fact]
    public async Task CompactContextProtectsEveryLatestPointFact()
    {
        var at = DateTimeOffset.UtcNow;
        var gateway = new CaptureGateway();
        var options = new AiOptions { ServerRenderedFacts = true };
        var engine = new LegacyInvestigationEngine(gateway, Options.Create(options));
        var pointId = Guid.NewGuid();
        var evidence = new[]
        {
            new InvestigationEvidence("E1", "Alarm", "Alarm", ["Historical alarm."], "hash", at, Guid.NewGuid()),
            new InvestigationEvidence("E2", "PointStatus", "Point", ["Old point state."], "hash", at, pointId,
                PointStatus: new() { PointId = pointId }),
            new InvestigationEvidence("E3", "PointStatus", "Point", ["Latest point state."], "hash", at, pointId,
                PointStatus: new() { PointId = pointId }),
            new InvestigationEvidence("E4", "PointStatus", "Other point", ["Other point state."], "hash", at, Guid.NewGuid(),
                PointStatus: new() { PointId = Guid.NewGuid() }),
            new InvestigationEvidence("E5", "Document", "Document heading", ["# Heading"], "hash", at, Guid.NewGuid()),
            new InvestigationEvidence("E6", "Document", "Document body", ["Read-only fixture procedure body."], "hash", at, Guid.NewGuid())
        };
        using var budget = new LlmBudget { EvidenceAsOf = at };
        await Assert.ThrowsAsync<LlmException>(() => engine.ExecuteAsync(evidence,
            (_, _, _) => Task.FromResult(new InvestigationToolResult([], [])), _ => Task.CompletedTask, budget, default));
        using var context = JsonDocument.Parse(gateway.Request!.Messages.Last().Content!);
        var protectedFacts = context.RootElement.GetProperty("requiredObservations").EnumerateArray()
            .Select(x => x.GetProperty("text").GetString()).ToArray();
        Assert.Contains("Latest point state.", protectedFacts);
        Assert.Contains("Other point state.", protectedFacts);
        Assert.DoesNotContain("Old point state.", protectedFacts);
        Assert.Contains("Read-only fixture procedure body.", protectedFacts);
    }

    [Fact]
    public void ServerRenderingSchemaForbidsEchoedFactsWithoutChangingStoredContract()
    {
        var original = InvestigationContract.OutputSchema.Schema.GetRawText();
        var compact = LegacyInvestigationEngine.ModelSchema(InvestigationContract.OutputSchema, true, true, LegacyInvestigationEngine.SafeSummary);
        var properties = compact.Schema.GetProperty("properties");
        Assert.Equal(0, properties.GetProperty("observations").GetProperty("maxItems").GetInt32());
        Assert.Equal(0, properties.GetProperty("dataWarnings").GetProperty("maxItems").GetInt32());
        Assert.Equal(2, properties.GetProperty("nextChecks").GetProperty("maxItems").GetInt32());
        Assert.Equal(LegacyInvestigationEngine.SafeSummary, properties.GetProperty("summary").GetProperty("enum")[0].GetString());
        Assert.False(properties.GetProperty("summary").TryGetProperty("maxLength", out _));
        Assert.Equal(original, InvestigationContract.OutputSchema.Schema.GetRawText());
        Assert.Same(InvestigationContract.OutputSchema,
            LegacyInvestigationEngine.ModelSchema(InvestigationContract.OutputSchema, false, true, LegacyInvestigationEngine.SafeSummary));
    }

    private sealed class CaptureGateway : ILlmGateway
    {
        public LlmRequest? Request
        {
            get; private set;
        }

        public Task<LlmCompletion> CompleteAsync(LlmRequest request, LlmBudget budget, CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromException<LlmCompletion>(new LlmException(LlmFailure.InvalidRequest));
        }
    }
}
