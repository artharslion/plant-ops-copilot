using Microsoft.Extensions.Options;
using PlantOps.AI;
using PlantOps.Application.AI;
using Xunit;

namespace PlantOps.Tests;

public sealed class InvestigationTrendAnalysisTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 28, 0, 0, 0, TimeSpan.Zero);
    private const string Fact = "首值 60 degC，末值 90 degC，变化量 30 degC。";

    private static InvestigationEvidence[] Evidence(bool rising = true)
    {
        var change = new InvestigationTrendChange(Guid.NewGuid(), Guid.NewGuid(), At, At.AddMinutes(1), 60, 90, 30, 30, 3, 2);
        var trend = new InvestigationDerivedTrend(InvestigationTrendPolicy.Version, Guid.NewGuid(), "TEMP", "degC",
            Guid.NewGuid(), "event", At, At.AddMinutes(2), At.AddMinutes(2), At.AddMinutes(3), [], [], [], change,
            rising ? [change] : [], []);
        return [new("E1", "Alarm", "Alarm", ["历史告警触发值为 90 degC。"], "a", At, trend.AlarmId),
            new("E2", "EventDerivedTrend", "Raw changes", [Fact], "b", At, trend.PointId, DerivedTrend: trend)];
    }

    private static InvestigationResult Answer(string text) => new("2", InvestigationEngine.SafeSummary,
        [new(Fact, ["E2"])], [], [], [], [], ["E1", "E2"], [new(text, ["E1", "E2"])]);

    [Fact]
    public void InterpretationCanLinkVerifiedRisingSequenceAndHistoricalAlarmWithoutInventingNumbers()
    {
        var result = InvestigationEngine.ValidateFacts(InvestigationContract.Serialize(Answer(
            "观测段呈非下降变化，为历史告警提供升温趋势依据；异常原因仍待核查。")), Evidence());
        Assert.Single(result.Analysis!);
    }

    [Theory]
    [InlineData("观测升温 99 度，已经证明故障原因。")]
    [InlineData("首值 60 degC，末值 90 degC，变化量 30 degC。")]
    [InlineData("温度变化说明设备完全安全，已查明故障原因。")]
    [InlineData("趋势已确认根因，维护人员可确认故障。")]
    public void FabricatedNumbersCopyOnlyAnswersAndDiagnosesAreRejected(string text)
    {
        Assert.Throws<LlmException>(() => InvestigationEngine.ValidateFacts(InvestigationContract.Serialize(Answer(text)), Evidence()));
    }

    [Fact]
    public void MinuteExtremaOrEndpointChangeAloneCannotGroundARisingSequence()
    {
        Assert.Throws<LlmException>(() => InvestigationEngine.ValidateFacts(InvestigationContract.Serialize(
            Answer("观测段呈非下降变化，为告警提供趋势依据，原因待核查。")), Evidence(false)));
        var evidence = Evidence().Select(x => x with { DerivedTrend = null }).ToArray();
        Assert.Throws<LlmException>(() => InvestigationEngine.ValidateFacts(InvestigationContract.Serialize(
            Answer("有序观测变化为所选告警提供趋势依据，原因待核查。")), evidence));
    }

    [Fact]
    public void VerifiableChangesRequireAnalysisAndBothRawAndAlarmReferences()
    {
        var answer = Answer("有序观测变化为所选告警提供趋势依据，原因待核查。");
        Assert.Throws<LlmException>(() => InvestigationEngine.ValidateFacts(InvestigationContract.Serialize(answer with { Analysis = [] }), Evidence()));
        Assert.Throws<LlmException>(() => InvestigationEngine.ValidateFacts(InvestigationContract.Serialize(answer with
        {
            Analysis = [new(answer.Analysis![0].Text, ["E2"])],
            EvidenceIds = ["E2"]
        }), Evidence()));
        Assert.Throws<LlmException>(() => InvestigationEngine.ValidateFacts(InvestigationContract.Serialize(answer with
        {
            SchemaVersion = "1",
            Analysis = null,
            EvidenceIds = ["E2"]
        }), Evidence()));
    }

    [Theory]
    [InlineData(false, "zh-CN")]
    [InlineData(true, "zh-CN")]
    [InlineData(false, "en")]
    [InlineData(true, "en")]
    public async Task EngineAndFakePreserveVersionedAnalysisInBothRenderingModes(bool serverFacts, string language)
    {
        var evidence = Evidence();
        evidence[1] = evidence[1] with
        {
            Facts = ["有序原始观测：" + Fact]
        };
        using var budget = new LlmBudget(new(MaxInputTokens: 16000, MaxOutputTokens: 2000));
        var result = await new InvestigationEngine(new FakeLlmGateway(), Options.Create(new AiOptions { ServerRenderedFacts = serverFacts }))
            .ExecuteAsync(evidence, (_, _, _) => throw new InvalidOperationException(), _ => Task.CompletedTask, budget, default, language);
        var parsed = InvestigationEngine.ValidateFacts(result.ResultJson, evidence, language: language);
        Assert.Equal("2", parsed.SchemaVersion);
        Assert.Single(parsed.Analysis!);
        Assert.Contains(parsed.Observations, x => x.EvidenceIds.Contains("E2"));
        Assert.Equal("investigation_v2", budget.Requests[0].GetProperty("schemaName").GetString());
        Assert.Contains(InvestigationTrendPolicy.Version, budget.Requests[0].GetProperty("evidencePolicyVersion").GetString());
    }
}
