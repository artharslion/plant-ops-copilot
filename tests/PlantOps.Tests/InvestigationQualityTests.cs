using System.Text.Json;
using Microsoft.Extensions.Options;
using PlantOps.AI;
using PlantOps.Application.AI;
using Xunit;

namespace PlantOps.Tests;

public sealed class InvestigationQualityTests
{
    private static readonly InvestigationEvidence Alarm = new("E1", "Alarm", "告警", ["告警曾触发。", "告警已恢复，根因未确认。"], "hash", DateTimeOffset.UtcNow, Guid.NewGuid());
    private static readonly InvestigationEvidence Raw = Alarm with { Id = "E2", Kind = "EventReadings", Facts = ["事件原始观测，质量 Good，来源与映射已记录。"] };
    private static readonly InvestigationEvidence Maintenance = Alarm with { Id = "E3", Kind = "Maintenance", Facts = ["维护记录：核查告警；状态为 Draft；处理结果摘录：未记录。"] };

    private static InvestigationResult Valid() => new("1", InvestigationEngine.SafeSummary, [new(Alarm.Facts[0], ["E1"])], [], [], [], []);

    [Fact]
    public void SummaryCanOnlyComposeUpToThreeCompleteObservationFacts()
    {
        var result = Valid() with
        {
            Summary = string.Join('\n', Alarm.Facts),
            Observations = Alarm.Facts.Select(x => new InvestigationObservation(x, ["E1"])).ToArray()
        };
        Assert.NotNull(InvestigationEngine.ValidateFacts(InvestigationContract.Serialize(result), [Alarm]));
        Assert.Equal(result.Summary, InvestigationEngine.ValidateFacts(InvestigationContract.Serialize(result with
        {
            Summary = result.Summary.Replace("\n", "\\n", StringComparison.Ordinal)
        }), [Alarm]).Summary);
        Assert.Throws<LlmException>(() => InvestigationEngine.ValidateFacts(InvestigationContract.Serialize(result with { Summary = result.Summary + "\n设备安全。" }), [Alarm]));
    }

    [Fact]
    public void AnEntireMultilineFactStillWorksAsASummaryWithoutRewritingItsContent()
    {
        var fact = Alarm with
        {
            Facts = ["资料章节\n完整事实，原文含有字面 \\n 标记。"]
        };
        var result = Valid() with
        {
            Summary = fact.Facts[0],
            Observations = [new(fact.Facts[0], ["E1"])]
        };
        Assert.Equal(fact.Facts[0], InvestigationEngine.ValidateFacts(InvestigationContract.Serialize(result), [fact]).Summary);
    }

    [Theory]
    [InlineData("事件原始观测有界节选为空。")]
    [InlineData("事件原始观测节选缺失。")]
    [InlineData("缺少维护记录事实。")]
    [InlineData("原始观测缺失，输入裁剪。")]
    public void ExistingFactsCannotBeReportedMissing(string claim)
    {
        Assert.Throws<LlmException>(() => InvestigationEngine.ValidateFacts(InvestigationContract.Serialize(Valid() with { MissingInformation = [claim] }), [Alarm, Raw, Maintenance]));
    }

    [Fact]
    public void PoorMaintenanceDetailsAndExplicitlyUnavailableRawRemainValidLimitations()
    {
        var unavailable = Raw with
        {
            Facts = ["事件原始观测不可查询；使用历史告警快照。"]
        };
        var result = Valid() with
        {
            MissingInformation = ["事件原始观测节选缺失。", "已有维护记录缺少有效处理结果，不能支持维修原因分析。"]
        };
        Assert.NotNull(InvestigationEngine.ValidateFacts(InvestigationContract.Serialize(result), [Alarm, unavailable, Maintenance]));
        var recovery = Raw with
        {
            Id = "E4",
            Kind = "RecoveryReadings"
        };
        Assert.NotNull(InvestigationEngine.ValidateFacts(InvestigationContract.Serialize(result), [Alarm, unavailable, recovery, Maintenance]));
    }

    [Fact]
    public void AvailableExcerptsDoNotProveCompleteRawHistory()
    {
        var result = Valid() with
        {
            MissingInformation = ["已有原始观测节选，仍缺少完整原始观测和物理采样语义。"]
        };
        Assert.NotNull(InvestigationEngine.ValidateFacts(InvestigationContract.Serialize(result), [Alarm, Raw]));
    }

    [Theory]
    [InlineData("若原始观测缺失，由维护工程师复核保留范围。")]
    [InlineData("如果缺少原始样本，则交维护工程师复核。")]
    [InlineData("如维护记录缺失，交维护工程师核查授权范围。")]
    [InlineData("一旦原始数据不可查询则交维护工程师核对保留范围。")]
    [InlineData("当原始观测缺失时，交维护工程师核查。")]
    public void ConditionalEscalationDoesNotAssertThatExistingEvidenceIsMissing(string escalation)
    {
        var check = new InvestigationCheck("核查事件。", ["E1"], "记录来源和时间。", "全部对应关系核对且差异已记录后完成。", escalation);
        Assert.NotNull(InvestigationEngine.ValidateFacts(InvestigationContract.Serialize(Valid() with
        {
            NextChecks = [check]
        }), [Alarm, Raw, Maintenance]));
    }

    [Theory]
    [InlineData("原始观测缺失，交维护工程师复核。")]
    [InlineData("维护记录缺失，交维护工程师复核。")]
    [InlineData("若出现异常，原始观测缺失，交维护工程师复核。")]
    [InlineData("若原始观测缺失，交维护工程师复核；现有原始观测缺失。")]
    [InlineData("若原始观测缺失，交维护工程师复核。维护记录缺失。")]
    public void EscalationStillRejectsContradictoryAssertionsOutsideTheConditionalAntecedent(string escalation)
    {
        var check = new InvestigationCheck("核查事件。", ["E1"], "记录来源和时间。", "全部对应关系核对且差异已记录后完成。", escalation);
        Assert.Throws<LlmException>(() => InvestigationEngine.ValidateFacts(InvestigationContract.Serialize(Valid() with { NextChecks = [check] }), [Alarm, Raw, Maintenance]));
    }

    [Fact]
    public void ManualAloneCannotSupportAnEventCauseHypothesis()
    {
        var manual = Raw with
        {
            Kind = "Document",
            Facts = ["温度变化只能形成待核查的关联。"]
        };
        var hypothesis = new InvestigationHypothesis("可能存在温度关联。", "weak", ["E2"], "核查同期趋势。", manual.Facts[0], "缺少同期观测。");
        Assert.Throws<LlmException>(() => InvestigationEngine.ValidateFacts(InvestigationContract.Serialize(Valid() with { Hypotheses = [hypothesis] }), [Alarm, manual]));
    }

    [Fact]
    public void UnqueryableRawIsALimitationRatherThanACauseBasis()
    {
        var unavailable = Raw with
        {
            Facts = ["事件原始观测不可查询；使用历史告警快照。"]
        };
        var hypothesis = new InvestigationHypothesis("可能存在通信故障。", "weak", ["E2"], "核对采集事件日志。", unavailable.Facts[0], "缺少独立故障记录。");
        Assert.Throws<LlmException>(() => InvestigationEngine.ValidateFacts(InvestigationContract.Serialize(Valid() with { Hypotheses = [hypothesis] }), [Alarm, unavailable]));
    }

    [Theory]
    [InlineData("未完成；待补证。")]
    [InlineData("部分完成；已看到观测。")]
    [InlineData("待核查。")]
    public void CompletionMustNotBeOnlyAnExecutionStatus(string completion)
    {
        var check = new InvestigationCheck("核查事件。", ["E1"], "记录来源和时间。", completion, "证据不足交工程师核查。");
        Assert.Throws<LlmException>(() => InvestigationEngine.ValidateFacts(InvestigationContract.Serialize(Valid() with { NextChecks = [check] }), [Alarm]));
        Assert.NotNull(InvestigationEngine.ValidateFacts(InvestigationContract.Serialize(Valid() with
        {
            NextChecks = [check with { Completion = "全部对应关系核对一致且差异已记录后完成。" }]
        }), [Alarm]));
    }

    [Fact]
    public void PackingReservesCoreFactsBeforeLargeManualChunks()
    {
        var docs = Enumerable.Range(4, 6).Select(i => Alarm with { Id = "E" + i, Kind = "Document", Facts = [new string('资', 900) + "完整片段末尾。"] });
        var packed = InvestigationEngine.ForModel([Alarm, Raw, Maintenance, .. docs], 8000);
        Assert.Equal(Raw.Facts, packed.Single(x => x.Id == Raw.Id).Facts);
        Assert.Equal(Maintenance.Facts, packed.Single(x => x.Id == Maintenance.Id).Facts);
        Assert.Contains(packed, x => x.Kind == "Document" && x.OmittedFactCount > 0);
    }

    [Fact]
    public async Task ServerRenderedFactsUseTheAuthorizedLedgerAndKeepTheCompatibleWireSchema()
    {
        LlmRequest? captured = null;
        var fake = new FakeLlmGateway();
        var gateway = new RequestCapture((request, budget, ct) =>
        {
            captured = request;
            return fake.CompleteAsync(request, budget, ct);
        });
        using var budget = new LlmBudget(new(MaxInputTokens: 32000, MaxOutputTokens: 6000));
        var result = await new InvestigationEngine(gateway, Options.Create(new AiOptions { ServerRenderedFacts = true })).ExecuteAsync([Alarm, Raw, Maintenance],
            (_, _, _) => throw new InvalidOperationException(), _ => Task.CompletedTask, budget, CancellationToken.None);
        var request = Assert.IsType<LlmRequest>(captured);
        using var input = JsonDocument.Parse(request.Messages.Single(x => x.Role == LlmRole.User).Content!);
        var required = input.RootElement.GetProperty("requiredObservations").EnumerateArray().Select(x => x.GetProperty("text").GetString()).ToArray();
        Assert.Equal("server", input.RootElement.GetProperty("factRendering").GetString());
        Assert.Equal(InvestigationContract.LegacyOutputSchema.Name, request.OutputSchema!.Name);
        var report = InvestigationContract.ParseAndValidateReferences(result.ResultJson, new HashSet<string>(["E1", "E2", "E3"]));
        Assert.Equal(required, report.Observations.Select(x => x.Text).ToArray());
        Assert.Contains(Raw.Facts[0], report.Observations.Select(x => x.Text));
        Assert.InRange(ChatCompletionsProtocol.Serialize(request, "test").Length + 256, 1, 32000);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ServerAssemblyNeverSilentlyDropsUnexpectedModelFactsOrRepairsForeignReferences(bool foreignReference)
    {
        var draft = foreignReference
            ? Valid() with
            {
                Observations = [],
                NextChecks = [new("核对事件。", ["FOREIGN"])],
                EvidenceIds = ["FOREIGN"]
            }
            : Valid();
        var gateway = new FakeLlmGateway([new(InvestigationContract.Serialize(draft), [], "fake", new(100, 20))]);
        using var budget = new LlmBudget(new(MaxInputTokens: 32000, MaxOutputTokens: 6000));
        await Assert.ThrowsAsync<LlmException>(() => new InvestigationEngine(gateway, Options.Create(new AiOptions { ServerRenderedFacts = true }))
            .ExecuteAsync([Alarm], (_, _, _) => throw new InvalidOperationException(), _ => Task.CompletedTask, budget, CancellationToken.None));
    }

    private sealed class RequestCapture(Func<LlmRequest, LlmBudget, CancellationToken, Task<LlmCompletion>> complete) : ILlmGateway
    {
        public Task<LlmCompletion> CompleteAsync(LlmRequest request, LlmBudget budget, CancellationToken cancellationToken = default) => complete(request, budget, cancellationToken);
    }

    [Fact]
    public async Task AServerRenderedReportCannotSucceedWithOnlyFactsAndNoInvestigationChecks()
    {
        var draft = Valid() with
        {
            Observations = []
        };
        var gateway = new FakeLlmGateway([new(InvestigationContract.Serialize(draft), [], "fake", new(100, 20))]);
        using var budget = new LlmBudget(new(MaxInputTokens: 32000, MaxOutputTokens: 6000));
        await Assert.ThrowsAsync<LlmException>(() => new InvestigationEngine(gateway, Options.Create(new AiOptions { ServerRenderedFacts = true }))
            .ExecuteAsync([Alarm], (_, _, _) => throw new InvalidOperationException(), _ => Task.CompletedTask, budget, CancellationToken.None));
    }

    [Fact]
    public async Task IdenticalUnavailableExcerptsRetainBothTriggerAndRecoveryReferences()
    {
        var unavailable = Raw with
        {
            Facts = ["事件原始观测不可查询；使用历史告警快照。"]
        };
        var recovery = unavailable with
        {
            Id = "E4",
            Kind = "RecoveryReadings"
        };
        using var budget = new LlmBudget(new(MaxInputTokens: 32000, MaxOutputTokens: 6000));
        var result = await new InvestigationEngine(new FakeLlmGateway(), Options.Create(new AiOptions { ServerRenderedFacts = true }))
            .ExecuteAsync([Alarm, unavailable, recovery], (_, _, _) => throw new InvalidOperationException(), _ => Task.CompletedTask, budget, CancellationToken.None);
        var report = InvestigationContract.ParseAndValidateReferences(result.ResultJson, new HashSet<string>(["E1", "E2", "E4"]));
        Assert.Equal(new[] { "E2", "E4" }, Assert.Single(report.Observations, x => x.Text == unavailable.Facts[0]).EvidenceIds);
        Assert.Contains(report.MissingInformation, x => x.StartsWith("触发事件的原始观测不可查询", StringComparison.Ordinal));
        Assert.Contains(report.MissingInformation, x => x.StartsWith("恢复事件的原始观测不可查询", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ServerScopeLimitationsSurviveAnEmptyModelMissingInformationList()
    {
        const string scopeFact = "调查取数范围：自动趋势仅覆盖所选告警点位，关联点位需经授权补查。";
        var scope = Alarm with
        {
            Id = "E4",
            Kind = "DataQuality",
            Facts = [scopeFact]
        };
        var check = new InvestigationCheck("核对事件与现场记录。", ["E1"], "记录时间和证据。", "全部记录经审核后完成。", "证据不足交工程师复核。");
        var draft = Valid() with
        {
            Observations = [],
            NextChecks = [check]
        };
        using var budget = new LlmBudget(new(MaxInputTokens: 32000, MaxOutputTokens: 6000));
        var gateway = new FakeLlmGateway([new(InvestigationContract.Serialize(draft), [], "fake", new(100, 20))]);
        var result = await new InvestigationEngine(gateway, Options.Create(new AiOptions { ServerRenderedFacts = true }))
            .ExecuteAsync([Alarm, scope], (_, _, _) => throw new InvalidOperationException(), _ => Task.CompletedTask, budget, CancellationToken.None);
        var report = InvestigationContract.ParseAndValidateReferences(result.ResultJson, new HashSet<string>(["E1", "E4"]));
        Assert.Contains(scopeFact, report.MissingInformation);
    }

    [Fact]
    public async Task RelatedComparisonFitsAlongsideSeparateEventRecoveryCurrentAndRawAnchors()
    {
        var trigger = Alarm with
        {
            Id = "E2",
            Kind = "TriggerReading",
            Facts = ["原始触发样本值为历史值。"]
        };
        var eventRaw = Raw with
        {
            Id = "E3",
            Facts = ["事件样本 " + trigger.ResourceId + "，质量 Good。"]
        };
        var recoveryRaw = Raw with
        {
            Id = "E4",
            Kind = "RecoveryReadings",
            Facts = ["恢复样本，质量 Good。"]
        };
        var trends = new[] { "EventTrend", "RecoveryTrend", "Trend" }.Select((kind, i) => Alarm with
        {
            Id = "E" + (i + 5),
            Kind = kind,
            Facts = [kind + " 最小值为已记录值。"]
        }).ToArray();
        var comparison = Alarm with
        {
            Id = "E8",
            Kind = "RelatedComparison",
            Facts = ["关联温度的事件与当前完成分钟对照已记录，因果待核实。"]
        };
        InvestigationEvidence[] evidence = [Alarm, trigger, eventRaw, recoveryRaw, .. trends, comparison];
        using var budget = new LlmBudget(new(MaxInputTokens: 32000, MaxOutputTokens: 6000));
        var result = await new InvestigationEngine(new FakeLlmGateway(), Options.Create(new AiOptions { ServerRenderedFacts = true }))
            .ExecuteAsync(evidence, (_, _, _) => throw new InvalidOperationException(), _ => Task.CompletedTask, budget, CancellationToken.None);
        var report = InvestigationContract.ParseAndValidateReferences(result.ResultJson, evidence.Select(x => x.Id).ToHashSet());
        Assert.Equal(8, report.Observations.Length);
        Assert.Contains(report.Observations, x => x.Text == comparison.Facts[0]);
        Assert.Contains(report.Observations, x => x.Text == eventRaw.Facts[0]);
        Assert.Contains(report.Observations, x => x.Text == recoveryRaw.Facts[0]);
        Assert.DoesNotContain(report.Observations, x => x.Text == trigger.Facts[0]);
    }
}
