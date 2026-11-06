using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using PlantOps.AI;
using PlantOps.Application.AI;
using Xunit;

namespace PlantOps.Tests;

// Deterministic v3 contract and scripted orchestration, not semantic model acceptance.
public sealed class InvestigationAdviceTests
{
    [Fact]
    public void V3EngineDoesNotExposeLegacyValidationEntrypoints()
    {
        var names = typeof(InvestigationEngine).GetMembers().Select(x => x.Name).ToArray();
        Assert.DoesNotContain("ValidateFacts", names);
        Assert.DoesNotContain("SafeSummary", names);
        Assert.DoesNotContain("EnglishSafeSummary", names);
    }

    private static readonly Guid Point = Guid.NewGuid();
    private static readonly InvestigationEvidence[] Evidence =
    [
        new("E1", "Alarm", "Alarm", ["历史告警。"], "h", DateTimeOffset.UtcNow, Guid.NewGuid()),
        new("E2", "PointStatus", "Vibration", ["当前振动可信且有异常证据。"], "h", DateTimeOffset.UtcNow, Point,
            PointStatus: new() { PointId = Point, CurrentTrust = "Trusted", CurrentAnomaly = "Present" }, State: new("Available", "Trusted", "Present")),
        new("D1", "Document", "Procedure", ["核对现场观测记录。"], "h", DateTimeOffset.UtcNow, Guid.NewGuid())
    ];

    internal static InvestigationAdvice Valid(string subject, string evidence = "E2") => new("3", "Ready", "已有明确的核查方向。", [],
        "当前振动异常需要核查，原因尚未确认。",
        [new("F1", [subject], "Current", "EquipmentCondition", "AnomalySupported", "当前观测支持振动异常。", [evidence])], [],
        [new("R1", "Inspect", [subject], [evidence], "核对现场独立振动观测与工况记录。", "记录对照结果和缺项。",
            "核查结果与缺项经维护工程师复核。", new("异常持续或证据冲突时提交复核。", "MaintenanceEngineer"))], [],
        new("DraftReady", "可以先形成诊断工单。", [], new("振动异常核查", "High", "当前异常需要优先核查。", "核实异常依据及原因线索。", ["R1"])));

    private static InvestigationAdvice Parse(InvestigationAdvice advice) => InvestigationAdviceContract.ParseAndValidate(
        InvestigationAdviceContract.Serialize(advice), Evidence);

    [Theory]
    [InlineData("不能据此确认设备安全。")]
    [InlineData("按既有停机授权流程提交维护工程师复核。")]
    [InlineData("不得写入设备；需要保留现场信息缺口。")]
    public void AdviceWordingDoesNotDetermineTheStructuredWorkflowStatus(string text)
    {
        var result = Parse(Valid(Point.ToString()) with
        {
            Summary = text
        });
        Assert.Equal("Ready", result.Status);
        Assert.Equal("DraftReady", result.WorkOrder.Status);
    }

    [Fact]
    public void GroundedAdviceProducesTheSameTaskAndPriorityInTheDraft()
    {
        var result = Parse(Valid(Point.ToString()));
        var draft = InvestigationDraftContent.Build(result, "zh-CN");
        Assert.Equal("振动异常核查", draft.Title);
        Assert.Equal("High", draft.Priority);
        Assert.Contains(result.Recommendations[0].Task, draft.Description);
        Assert.Contains("[E2]", draft.Description);
        Assert.Null(draft.AssignedToUserId);
    }

    [Theory]
    [InlineData("NeedsMoreEvidence", "DraftReady")]
    [InlineData("NeedsMoreEvidence", "NeedsInformation")]
    [InlineData("NeedsExpertReview", "NotRecommended")]
    public void AdviceReadinessIsIndependentOfDraftReadiness(string status, string draftStatus)
    {
        var advice = Valid(Point.ToString());
        var gap = new InvestigationGap("G1", "RequiresOnSite", "缺少现场工况。", "限制原因判断。", [], ["R1"]);
        advice = advice with
        {
            Status = status,
            BlockingGapIds = status == "NeedsMoreEvidence" ? ["G1"] : [],
            Gaps = [gap],
            WorkOrder = advice.WorkOrder with
            {
                Status = draftStatus,
                BlockingGapIds = draftStatus == "NeedsInformation" ? ["G1"] : [],
                Draft = draftStatus == "DraftReady" ? advice.WorkOrder.Draft : null
            }
        };
        Assert.Equal(status, Parse(advice).Status);
        if (draftStatus != "DraftReady")
        {
            Assert.Throws<InvalidOperationException>(() => InvestigationDraftContent.Build(advice, "zh-CN"));
        }
    }

    [Theory]
    [InlineData("blocked-ready")]
    [InlineData("missing-blocker")]
    [InlineData("foreign-evidence")]
    [InlineData("foreign-subject")]
    [InlineData("foreign-recommendation")]
    [InlineData("duplicate-id")]
    [InlineData("draft-without-tasks")]
    [InlineData("draft-on-not-recommended")]
    [InlineData("missing-draft")]
    [InlineData("current-denies-known-anomaly")]
    public void InvalidStructureAndCrossReferencesAreRejected(string scenario)
    {
        var valid = Valid(Point.ToString());
        var invalid = scenario switch
        {
            "blocked-ready" => valid with { BlockingGapIds = ["G1"], Gaps = [new("G1", "Missing", "缺少记录。", "限制判断。", [], ["R1"])] },
            "missing-blocker" => valid with { Status = "NeedsMoreEvidence" },
            "foreign-evidence" => valid with { Findings = [valid.Findings[0] with { EvidenceRefs = ["SECRET"] }] },
            "foreign-subject" => valid with { Findings = [valid.Findings[0] with { SubjectRefs = [Guid.NewGuid().ToString()] }] },
            "foreign-recommendation" => valid with { WorkOrder = valid.WorkOrder with { Draft = valid.WorkOrder.Draft! with { RecommendationIds = ["R99"] } } },
            "duplicate-id" => valid with { Recommendations = [valid.Recommendations[0], valid.Recommendations[0]] },
            "draft-without-tasks" => valid with { WorkOrder = valid.WorkOrder with { Draft = valid.WorkOrder.Draft! with { RecommendationIds = [] } } },
            "draft-on-not-recommended" => valid with { WorkOrder = valid.WorkOrder with { Status = "NotRecommended" } },
            "missing-draft" => valid with { WorkOrder = valid.WorkOrder with { Draft = null } },
            _ => valid with { Findings = [valid.Findings[0] with { Status = "NoAnomalyFound" }] }
        };
        Assert.Throws<LlmException>(() => Parse(invalid));
    }

    [Theory]
    [InlineData("status", "Invented")]
    [InlineData("findings", null)]
    [InlineData("schemaVersion", "2")]
    [InlineData("unexpected", "value")]
    public void StrictSchemaRejectsUnknownFieldsEnumsAndNullArrays(string field, string? value)
    {
        var json = JsonNode.Parse(InvestigationAdviceContract.Serialize(Valid(Point.ToString())))!;
        json[field] = value;
        Assert.Throws<LlmException>(() => InvestigationAdviceContract.ParseAndValidate(json.ToJsonString(), Evidence));
    }

    [Fact]
    public void BadTemperatureDoesNotInvalidateIndependentVibrationFindings()
    {
        var badPoint = Guid.NewGuid();
        var bad = new InvestigationEvidence("E3", "PointStatus", "Temperature", ["当前温度不可验证。"], "h", DateTimeOffset.UtcNow, badPoint,
            PointStatus: new()
            {
                PointId = badPoint,
                CurrentTrust = "Unverifiable",
                CurrentAnomaly = "Insufficient"
            });
        var advice = Valid(Point.ToString()) with
        {
            Findings = [Valid(Point.ToString()).Findings[0], new("F2", [badPoint.ToString()], "Current", "EquipmentCondition", "Inconclusive", "温度观测不能支持当前温度判断。", ["E3"])]
        };
        Assert.Equal(2, InvestigationAdviceContract.ParseAndValidate(InvestigationAdviceContract.Serialize(advice), [.. Evidence, bad]).Findings.Length);
        Assert.Throws<LlmException>(() => InvestigationAdviceContract.ParseAndValidate(InvestigationAdviceContract.Serialize(advice with
        {
            Findings = [advice.Findings[1] with { Status = "NoAnomalyFound" }]
        }), [.. Evidence, bad]));
    }

    [Fact]
    public void PointFindingTargetsExcludeParentAssetContext()
    {
        var asset = new InvestigationEvidence("A1", "Asset", "Pump", ["Scoped pump."], "h", DateTimeOffset.UtcNow, Guid.NewGuid());
        var advice = Valid(Point.ToString()) with
        {
            Findings = [Valid(Point.ToString()).Findings[0] with { SubjectRefs = [Point.ToString(), asset.ResourceId.ToString()] }]
        };
        Assert.Throws<LlmException>(() => InvestigationAdviceContract.ParseAndValidate(InvestigationAdviceContract.Serialize(advice), [.. Evidence, asset]));
        var allowed = InvestigationAdviceContract.ForModel([.. Evidence, asset]).Schema.GetProperty("$defs")
            .GetProperty("findingSubjects").GetProperty("items").GetProperty("enum")
            .EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Contains(Point.ToString(), allowed);
        Assert.DoesNotContain(asset.ResourceId.ToString(), allowed);
    }

    [Fact]
    public void ManualAloneCannotBecomeACauseHypothesis()
    {
        var advice = Valid(Point.ToString()) with
        {
            Hypotheses = [new("H1", "原因待核查。", "Weak", ["D1"], [], ["R1"])]
        };
        Assert.Throws<LlmException>(() => Parse(advice));
    }

    [Fact]
    public void InconclusiveWindowFindingDoesNotNeedToRestateTheLatestPointSnapshot()
    {
        var window = new InvestigationEvidence("W1", "Trend", "Current window", ["Current window coverage is incomplete."],
            "h", DateTimeOffset.UtcNow, Point);
        var advice = Valid(Point.ToString());
        advice = advice with
        {
            Findings = [.. advice.Findings, new("F2", [Point.ToString()], "Current", "EquipmentCondition", "Inconclusive",
                "Incomplete window coverage cannot establish equipment condition.", [window.Id])]
        };
        Assert.Equal(2, InvestigationAdviceContract.ParseAndValidate(InvestigationAdviceContract.Serialize(advice), [.. Evidence, window]).Findings.Length);
        Assert.Throws<LlmException>(() => InvestigationAdviceContract.ParseAndValidate(InvestigationAdviceContract.Serialize(advice with
        {
            Findings = [advice.Findings[1] with { Status = "AnomalySupported" }]
        }), [.. Evidence, window]));
    }

    [Fact]
    public void BoundedNoAnomalyFindingCanRecommendNoWorkOrderWithoutInventingTasks()
    {
        var point = Evidence[1] with
        {
            PointStatus = Evidence[1].PointStatus! with
            {
                CurrentAnomaly = "NotFound"
            },
            State = new("Available", "Trusted", "NotFound")
        };
        var advice = Valid(Point.ToString()) with
        {
            Findings = [Valid(Point.ToString()).Findings[0] with { Status = "NoAnomalyFound" }],
            Recommendations = [],
            WorkOrder = new("NotRecommended", "当前检查范围内暂不需要额外任务。", [], null)
        };
        Assert.Empty(InvestigationAdviceContract.ParseAndValidate(InvestigationAdviceContract.Serialize(advice), [Evidence[0], point]).Recommendations);
    }

    [Fact]
    public async Task ModelCanFinalizeFromInitialEvidenceWithoutAForcedMeasurementTool()
    {
        using var budget = new LlmBudget() { EvidenceAsOf = DateTimeOffset.UtcNow };
        var verboseEvidence = Evidence.Select(x => x.PointStatus == null ? x : x with
        {
            Facts = [new string('x', 6000)]
        }).ToArray();
        var gateway = new ScriptedLlmGateway([
            new(InvestigationAdviceContract.Serialize(Valid(Point.ToString())), [], "fixture", new(6000, 50))
        ]);
        var result = await new InvestigationEngine(gateway, Options.Create(new AiOptions { Provider = "OpenAiCompatible" })).ExecuteAsync(verboseEvidence,
            (_, _, _) => throw new InvalidOperationException("No extra reads are needed for this scoped review."), _ => Task.CompletedTask,
            budget, CancellationToken.None);
        Assert.Equal("Ready", JsonDocument.Parse(result.ResultJson).RootElement.GetProperty("status").GetString());
        Assert.Equal(1, budget.ModelRounds);
    }

    [Fact]
    public async Task IncompleteButValidAdviceCompletesTheModelRunWithoutAnotherRetry()
    {
        var advice = Valid(Point.ToString()) with
        {
            Status = "NeedsMoreEvidence",
            BlockingGapIds = ["G1"],
            Gaps = [new("G1", "RequiresOnSite", "需要现场工况。", "限制原因判断。", [], ["R1"])]
        };
        using var budget = new LlmBudget(new(MaxInputTokens: 32000, MaxOutputTokens: 6000));
        var gateway = new ScriptedLlmGateway([new(InvestigationAdviceContract.Serialize(advice), [], "fixture", new(100, 50))]);
        var result = await new InvestigationEngine(gateway, Options.Create(new AiOptions())).ExecuteAsync(Evidence,
            (_, _, _) => throw new InvalidOperationException(), _ => Task.CompletedTask, budget, CancellationToken.None);
        Assert.Equal("NeedsMoreEvidence", JsonDocument.Parse(result.ResultJson).RootElement.GetProperty("status").GetString());
        Assert.Equal(1, budget.ModelRounds);
    }

    [Fact]
    public async Task V3ScriptedReadThenFinalAnswerUsesTwoRounds()
    {
        using var budget = new LlmBudget(new(MaxInputTokens: 32000, MaxOutputTokens: 6000));
        var gateway = new ScriptedLlmGateway([
            new(null, [new("read", "get_recent_alarms", "{}")], "fixture", new(100, 20)),
            new(InvestigationAdviceContract.Serialize(Valid(Point.ToString())), [], "fixture", new(100, 50))
        ]);
        var reads = new List<string>();
        var result = await new InvestigationEngine(gateway, Options.Create(new AiOptions())).ExecuteAsync(Evidence,
            (name, _, _) =>
            {
                reads.Add(name);
                return Task.FromResult(new InvestigationToolResult([], []));
            }, _ => Task.CompletedTask, budget, CancellationToken.None);
        Assert.Equal(["get_recent_alarms"], reads);
        Assert.Equal(2, budget.ModelRounds);
        Assert.Equal(1, budget.ToolCalls);
        Assert.Equal("3", JsonDocument.Parse(result.ResultJson).RootElement.GetProperty("schemaVersion").GetString());
    }

    [Fact]
    public async Task V3RejectsReadWhenFinalRoundIsReserved()
    {
        using var budget = new LlmBudget(new(MaxInputTokens: 32000, MaxOutputTokens: 6000, MaxModelRounds: 1));
        var gateway = new ScriptedLlmGateway([
            new(null, [new("read", "get_recent_alarms", "{}")], "fixture", new(100, 20))
        ]);
        var error = await Assert.ThrowsAsync<LlmException>(() =>
            new InvestigationEngine(gateway, Options.Create(new AiOptions())).ExecuteAsync(Evidence,
                (_, _, _) => throw new InvalidOperationException("No reads allowed in the final round."),
                _ => Task.CompletedTask, budget, CancellationToken.None));
        Assert.Equal(LlmFailure.InvalidRequest, error.Failure);
        Assert.Equal(1, budget.ModelRounds);
    }

    [Fact]
    public async Task ForbiddenWritesAreRejectedBeforeCallingTheTool()
    {
        using var budget = new LlmBudget(new(MaxInputTokens: 32000, MaxOutputTokens: 6000));
        var gateway = new ScriptedLlmGateway([new(null, [new("attack", "opcua_write", "{}")], "fixture", new(100, 20))]);
        var called = false;
        var error = await Assert.ThrowsAsync<LlmException>(() => new InvestigationEngine(gateway, Options.Create(new AiOptions())).ExecuteAsync(Evidence,
            (_, _, _) =>
            {
                called = true;
                throw new InvalidOperationException();
            }, _ => Task.CompletedTask, budget, CancellationToken.None));
        Assert.Equal(LlmFailure.InvalidRequest, error.Failure);
        Assert.False(called);
    }
}
