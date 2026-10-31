using Microsoft.Extensions.Options;
using System.Text.Json;
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
    public async Task ServerRenderingCompactsRepeatedRawFactsAfterLargeReportedFirstRoundUsage()
    {
        var evidence = Evidence().Concat(Enumerable.Range(3, 25).Select(i => new InvestigationEvidence(
            "E" + i, "EventReadings", "Bounded raw observations", [new string('样', 300) + "。"], "hash", At, Guid.NewGuid()))).ToArray();
        var answer = Answer("有序观测变化为历史告警提供趋势依据；原因仍待核查。") with
        {
            Observations = [],
            NextChecks = [new("核对事件期观测。", ["E1"], "记录证据与缺项。", "全部证据经维护人员复核后完成。", "缺项交维护工程师核查。")]
        };
        var gateway = new FakeLlmGateway([
            new(null, [new("measurements", "get_recent_measurements", "{}")], "fake", new(7470, 16)),
            new(InvestigationContract.Serialize(answer), [], "fake", new(1000, 100))]);
        using var budget = new LlmBudget { EvidenceAsOf = At.AddMinutes(2) };
        var engine = new LegacyInvestigationEngine(gateway, Options.Create(new AiOptions { ServerRenderedFacts = true }));
        var result = await engine.ExecuteAsync([evidence[0]], (_, _, _) => Task.FromResult(new InvestigationToolResult(evidence.Skip(1).ToArray(), [])),
            _ => Task.CompletedTask, budget, CancellationToken.None);
        Assert.Equal(2, budget.ModelRounds);
        Assert.Equal(8470, budget.ReservedInputTokens);
        Assert.NotEmpty(JsonDocument.Parse(result.ResultJson).RootElement.GetProperty("analysis").EnumerateArray());
    }

    [Fact]
    public void InterpretationCanLinkVerifiedRisingSequenceAndHistoricalAlarmWithoutInventingNumbers()
    {
        var result = InvestigationEngine.ValidateFacts(InvestigationContract.Serialize(Answer(
            "观测段呈非下降变化，为历史告警提供升温趋势依据；异常原因仍待核查。")), Evidence());
        Assert.Single(result.Analysis!);
    }

    [Theory]
    [InlineData("观测段为历史告警提供趋势依据，但不能判断设备安全。")]
    [InlineData("观测段为历史告警提供趋势依据，无法据此确认设备安全。")]
    [InlineData("观测段为历史告警提供趋势依据，当前状态、事件成因及设备安全仍需现场验证。")]
    [InlineData("观测段为历史告警提供趋势依据，不能证明异常持续、原因或设备安全。")]
    [InlineData("观测段为历史告警提供趋势依据，不能据此推断当前状态、持续超限或设备安全。")]
    [InlineData("观测段为历史告警提供趋势依据，也不能证明当前仍持续超限或设备安全。")]
    [InlineData("观测段为历史告警提供趋势依据，不能仅凭当前可信值确认设备安全。")]
    public void ExplicitSafetyUncertaintyIsNotAnAffirmativeSafetyClaim(string text)
    {
        Assert.Single(InvestigationEngine.ValidateFacts(InvestigationContract.Serialize(Answer(text)), Evidence()).Analysis!);
    }

    [Theory]
    [InlineData("只读记录质量，不得写入或控制设备。", true)]
    [InlineData("只读记录质量，禁止写入设备。", true)]
    [InlineData("记录质量；不写入或更改系统。", true)]
    [InlineData("只读核对；不修改阈值。", true)]
    [InlineData("只读核对，不调整阈值、不控制设备。", true)]
    [InlineData("记录返回信息；不得写入告警、工单或设备控制状态。", true)]
    [InlineData("不执行写入或控制操作。", true)]
    [InlineData("由现场负责人按既有停机授权流程升级。", true)]
    [InlineData("若无授权资料，应记录未找到并停止规程性推断。", true)]
    [InlineData("不修改阈值；将输出调整为 99。", false)]
    [InlineData("不得写入或控制设备；将阈值改为 99。", false)]
    public void LegacyAdviceKeepsProhibitionsAndRejectsUngroundedNumbers(string record, bool accepted)
    {
        var answer = Answer("历史变化为告警提供依据，仍需现场核查原因。") with
        {
            NextChecks = [new("只读核对记录。", ["E1"], record, "记录经维护人员核对后完成。", "缺项交维护负责人。")]
        };
        if (accepted)
        {
            InvestigationEngine.ValidateFacts(InvestigationContract.Serialize(answer), Evidence());
        }
        else
        {
            Assert.Throws<LlmException>(() => InvestigationEngine.ValidateFacts(InvestigationContract.Serialize(answer), Evidence()));
        }
    }

    [Fact]
    public void CurrentInterpretationUsesLatestPointAndAssessmentWithoutPretendingToBeATrend()
    {
        var point = Guid.NewGuid();
        var current = new InvestigationEvidence("E3", "PointStatus", "Current", ["振动测点当前可信且有异常证据。"], "c", At, point,
            PointStatus: new()
            {
                PointId = point,
                CurrentTrust = "Trusted",
                CurrentAnomaly = "Present"
            });
        var assessment = new InvestigationEvidence("E4", "Assessment", "Assessment", ["仅限已检查范围。"], "d", At, point);
        var evidence = Evidence().Concat([current, assessment]).ToArray();
        var answer = Answer("历史变化为告警提供依据，原因仍需现场核查。") with
        {
            Analysis = [new("历史变化为告警提供依据，原因仍需现场核查。", ["E1", "E2"]),
                new("振动测点当前可信且有异常证据。当前异常需保留，不能因其他测点不可验证而否定。", ["E3", "E4"])],
            EvidenceIds = ["E1", "E2", "E3", "E4"]
        };
        Assert.Equal(2, InvestigationEngine.ValidateFacts(InvestigationContract.Serialize(answer), evidence).Analysis!.Length);
        var replacement = current with
        {
            Id = "E5",
            Facts = ["当前测点不可验证。"]
        };
        Assert.Throws<LlmException>(() => InvestigationEngine.ValidateFacts(InvestigationContract.Serialize(answer), [.. evidence, replacement]));
    }

    [Theory]
    [InlineData("观测升温 99 度，已经证明故障原因。")]
    [InlineData("首值 60 degC，末值 90 degC，变化量 30 degC。")]
    public void LegacyFabricatedNumbersAndCopyOnlyAnalysisAreRejected(string text)
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
        var result = await new LegacyInvestigationEngine(new FakeLlmGateway(), Options.Create(new AiOptions { ServerRenderedFacts = serverFacts }))
            .ExecuteAsync(evidence, (_, _, _) => throw new InvalidOperationException(), _ => Task.CompletedTask, budget, default, language);
        var parsed = InvestigationEngine.ValidateFacts(result.ResultJson, evidence, language: language);
        Assert.Equal("2", parsed.SchemaVersion);
        Assert.Single(parsed.Analysis!);
        Assert.Contains(parsed.Observations, x => x.EvidenceIds.Contains("E2"));
        Assert.Equal("investigation_v2", budget.Requests[0].GetProperty("schemaName").GetString());
        Assert.Contains(InvestigationTrendPolicy.Version, budget.Requests[0].GetProperty("evidencePolicyVersion").GetString());
    }
}
