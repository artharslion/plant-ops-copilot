using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using PlantOps.AI;
using PlantOps.Application.AI;
using Xunit;

namespace PlantOps.Tests;

public sealed class InvestigationAdviceSchemaTests
{
    private static InvestigationAdvice Advice(Guid point, string id)
    {
        var advice = InvestigationAdviceTests.Valid(point.ToString(), id);
        return advice with
        {
            Findings = [advice.Findings[0] with { Scope = "AlarmEvent" }]
        };
    }

    [Fact]
    public void AffirmativeCurrentFindingCannotUseAnAlarmAsAnAdditionalPointTarget()
    {
        var point = Guid.NewGuid();
        var alarm = Guid.NewGuid();
        InvestigationEvidence[] evidence =
        [
            new("alarm-record", "Alarm", "Alarm", ["Historical event"], "h", DateTimeOffset.UtcNow, alarm),
            new("current-snapshot", "PointStatus", "Point", ["Current observation"], "h", DateTimeOffset.UtcNow, point,
                PointStatus: new() { PointId = point, CurrentTrust = "Trusted", CurrentAnomaly = "Present" })
        ];
        var advice = InvestigationAdviceTests.Valid(point.ToString(), evidence[1].Id);
        Assert.NotNull(InvestigationAdviceContract.ParseAndValidate(InvestigationAdviceContract.Serialize(advice), evidence));
        var invalid = advice with
        {
            Findings = [advice.Findings[0] with { SubjectRefs = [point.ToString(), alarm.ToString()] }]
        };
        Assert.True(ModelAccepts(advice, evidence));
        Assert.False(ModelAccepts(invalid, evidence));
        var error = Assert.Throws<LlmException>(() => InvestigationAdviceContract.ParseAndValidate(InvestigationAdviceContract.Serialize(invalid), evidence));
        Assert.Equal("advice-current-evidence", error.ValidationRule);
    }

    [Fact]
    public void FindingBranchesUseLatestTrustedSnapshotsAndKeepOtherScopesAvailable()
    {
        var firstAlarm = Guid.NewGuid();
        var otherAlarm = Guid.NewGuid();
        var active = Guid.NewGuid();
        var stable = Guid.NewGuid();
        var downgraded = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        InvestigationEvidence Point(string id, Guid point, string trust, string anomaly, DateTimeOffset time) =>
            new(id, "PointStatus", "Point", ["Observation"], "h", time, point,
                PointStatus: new()
                {
                    PointId = point,
                    CurrentTrust = trust,
                    CurrentAnomaly = anomaly
                });
        InvestigationEvidence[] evidence =
        [
            new("first-event", "Alarm", "Alarm", ["Historical event"], "h", now, firstAlarm),
            new("other-event", "Alarm", "Alarm", ["Historical event"], "h", now, otherAlarm),
            Point("active-snapshot", active, "Trusted", "Present", now),
            Point("stable-snapshot", stable, "Trusted", "NotFound", now),
            Point("superseded-snapshot", downgraded, "Trusted", "NotFound", now),
            Point("unverified-snapshot", downgraded, "Unverifiable", "Insufficient", now.AddSeconds(-1))
        ];
        var advice = Advice(firstAlarm, evidence[0].Id);
        void Check(string scope, string domain, string status, Guid[] subjects, string[] refs, bool expected)
        {
            var candidate = advice with
            {
                Findings = [advice.Findings[0] with { Scope = scope, Domain = domain, Status = status,
                    SubjectRefs = subjects.Select(x => x.ToString()).ToArray(), EvidenceRefs = refs }]
            };
            Assert.Equal(expected, ModelAccepts(candidate, evidence));
            var error = Record.Exception(() => InvestigationAdviceContract.ParseAndValidate(InvestigationAdviceContract.Serialize(candidate), evidence));
            if (expected)
            {
                Assert.Null(error);
            }
            else
            {
                Assert.Equal("advice-current-evidence", Assert.IsType<LlmException>(error).ValidationRule);
            }
        }
        Check("AlarmEvent", "EquipmentCondition", "AnomalySupported", [firstAlarm, otherAlarm], ["first-event", "other-event"], true);
        Check("Recovery", "EquipmentCondition", "NoAnomalyFound", [otherAlarm, active], ["other-event", "active-snapshot"], true);
        Check("Current", "MeasurementChain", "AnomalySupported", [otherAlarm, downgraded], ["other-event", "unverified-snapshot"], true);
        Check("Current", "EquipmentCondition", "Inconclusive", [firstAlarm, downgraded], ["first-event", "unverified-snapshot"], true);
        Check("Current", "EquipmentCondition", "AnomalySupported", [active, stable], ["active-snapshot", "stable-snapshot"], true);
        Check("Current", "EquipmentCondition", "NoAnomalyFound", [stable], ["stable-snapshot"], true);
        Check("Current", "EquipmentCondition", "NoAnomalyFound", [active, stable], ["active-snapshot", "stable-snapshot"], false);
        Check("Current", "EquipmentCondition", "AnomalySupported", [downgraded], ["superseded-snapshot"], false);

        var missingLatestCitation = advice with
        {
            Findings = [advice.Findings[0] with { Scope = "Current", SubjectRefs = [active.ToString(), stable.ToString()],
                EvidenceRefs = ["active-snapshot"] }]
        };
        Assert.True(ModelAccepts(missingLatestCitation, evidence));
        var error = Assert.Throws<LlmException>(() => InvestigationAdviceContract.ParseAndValidate(
            InvestigationAdviceContract.Serialize(missingLatestCitation), evidence));
        Assert.Equal("advice-current-evidence", error.ValidationRule);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NoTrustedCurrentPointsLeavesOnlyInconclusiveCurrentEquipmentFindings(bool hasSnapshot)
    {
        var alarm = Guid.NewGuid();
        var point = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var eventEvidence = new InvestigationEvidence("event", "Alarm", "Alarm", ["Historical event"], "h", now, alarm);
        var trendEvidence = new InvestigationEvidence("event-window", "DerivedTrend", "Point", ["Historical observation"], "h", now, point,
            DerivedTrend: new("test", point, "PT", "unit", alarm, "AlarmEvent", now.AddMinutes(-1), now, now, now, [], [], [], null, [], []));
        var pointEvidence = new InvestigationEvidence("unverified", "PointStatus", "Point", ["Unverifiable observation"], "h", now, point,
            PointStatus: new()
            {
                PointId = point,
                CurrentTrust = "Unverifiable",
                CurrentAnomaly = "Insufficient"
            });
        InvestigationEvidence[] evidence = hasSnapshot ? [eventEvidence, trendEvidence, pointEvidence] : [eventEvidence, trendEvidence];
        var advice = Advice(alarm, eventEvidence.Id);
        Assert.True(ModelAccepts(advice, evidence));
        var current = advice.Findings[0] with
        {
            Scope = "Current",
            SubjectRefs = [point.ToString()],
            EvidenceRefs = [hasSnapshot ? pointEvidence.Id : trendEvidence.Id]
        };
        foreach (var status in new[] { "AnomalySupported", "NoAnomalyFound", "Inconclusive" })
        {
            Assert.Equal(status == "Inconclusive", ModelAccepts(advice with
            {
                Findings = [current with { Status = status }]
            }, evidence));
        }
        Assert.Equal(3, InvestigationAdviceContract.ForModel(evidence).Schema.GetProperty("properties").GetProperty("findings")
            .GetProperty("items").GetProperty("anyOf").GetArrayLength());
    }

    [Theory]
    [InlineData("reading-alpha", "gap-later")]
    [InlineData("G42", "E77")]
    public void EvidenceMembershipUsesAuthorizedIdsRatherThanTheirSpelling(string evidenceId, string gapId)
    {
        var point = Guid.NewGuid();
        InvestigationEvidence[] evidence = [new(evidenceId, "Alarm", "Snapshot", ["Observation"], "hash", DateTimeOffset.UtcNow, point)];
        var schema = InvestigationAdviceContract.ForModel(evidence).Schema;
        var allowed = schema.GetProperty("$defs").GetProperty("evidenceId").GetProperty("enum");
        Assert.Equal(evidenceId, Assert.Single(allowed.EnumerateArray()).GetString());
        foreach (var field in new[] { "evidenceRefs", "supportingEvidenceRefs", "contradictingEvidenceRefs" })
        {
            var matching = FindFields(JsonNode.Parse(schema.GetRawText())!, field).ToArray();
            Assert.NotEmpty(matching);
            foreach (var item in matching)
            {
                Assert.True(Accepts(JsonSerializer.SerializeToElement(new[] { evidenceId }), item, schema));
                Assert.False(Accepts(JsonSerializer.SerializeToElement(new[] { gapId }), item, schema));
            }
        }
        var advice = Advice(point, evidenceId) with
        {
            Gaps = [new(gapId, "Missing", "Missing context", "Limits assessment", [], ["R1"])],
            Status = "NeedsMoreEvidence",
            BlockingGapIds = [gapId]
        };
        Assert.True(ModelAccepts(advice, evidence));
        Assert.Equal(gapId, Assert.Single(InvestigationAdviceContract.ParseAndValidate(InvestigationAdviceContract.Serialize(advice), evidence).BlockingGapIds));
    }

    [Fact]
    public void ModelSubjectAliasesResolveToStoredIdsAndRejectPlaceholderOrDisallowedTargets()
    {
        var alarm = Guid.NewGuid();
        var asset = Guid.NewGuid();
        InvestigationEvidence[] evidence =
        [
            new("E1", "Alarm", "Alarm", ["Observation"], "h", DateTimeOffset.UtcNow, alarm),
            new("E2", "Asset", "Pump", ["Asset context"], "h", DateTimeOffset.UtcNow, asset)
        ];
        var subjects = new InvestigationModelSubjects(evidence);
        var advice = Advice(alarm, "E1");
        var wire = advice with
        {
            Findings = [advice.Findings[0] with { SubjectRefs = [subjects.AliasFor(alarm)!] }],
            Recommendations = [advice.Recommendations[0] with { SubjectRefs = [subjects.AliasFor(asset)!] }]
        };
        var schema = InvestigationAdviceContract.ForModel(evidence, subjects).Schema;
        var root = JsonNode.Parse(schema.GetRawText())!;
        InvestigationAdvice Parse(InvestigationAdvice candidate) => InvestigationAdviceContract.ParseModelResponse(
            InvestigationAdviceContract.Serialize(candidate), evidence, subjects);
        bool SchemaAccepts(InvestigationAdvice candidate) => Accepts(JsonSerializer.SerializeToElement(candidate,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)), root, schema);

        Assert.True(SchemaAccepts(wire));
        var stored = Parse(wire);
        Assert.Equal(alarm.ToString(), Assert.Single(stored.Findings[0].SubjectRefs));
        Assert.Equal(asset.ToString(), Assert.Single(stored.Recommendations[0].SubjectRefs));
        foreach (var invalidRef in new[] { Guid.Empty.ToString(), "S99", subjects.AliasFor(asset)! })
        {
            var invalid = wire with
            {
                Findings = [wire.Findings[0] with { SubjectRefs = [invalidRef] }]
            };
            Assert.False(SchemaAccepts(invalid));
            Assert.Equal("$.findings[0].subjectRefs", Assert.Throws<LlmException>(() => Parse(invalid)).ValidationDetails!.Path);
        }
        var badRecommendation = wire with
        {
            Recommendations = [wire.Recommendations[0] with { SubjectRefs = [Guid.Empty.ToString()] }]
        };
        Assert.False(SchemaAccepts(badRecommendation));
        Assert.Equal("$.recommendations[0].subjectRefs", Assert.Throws<LlmException>(() => Parse(badRecommendation)).ValidationDetails!.Path);
    }

    [Theory]
    [InlineData("Ready", false, true)]
    [InlineData("Ready", true, false)]
    [InlineData("NeedsMoreEvidence", false, false)]
    [InlineData("NeedsMoreEvidence", true, true)]
    [InlineData("NeedsExpertReview", false, true)]
    [InlineData("NeedsExpertReview", true, true)]
    public void FlatStatusRelationshipsRemainStrictServerInvariants(string status, bool blocker, bool accepted)
    {
        var point = Guid.NewGuid();
        InvestigationEvidence[] evidence = [new("sample-independent", "Alarm", "Snapshot", ["Observation"], "h", DateTimeOffset.UtcNow, point)];
        var advice = Advice(point, evidence[0].Id) with
        {
            Status = status,
            BlockingGapIds = blocker ? ["missing-context"] : [],
            Gaps = [new("missing-context", "Missing", "Context", "Limits assessment", [], ["R1"])]
        };
        var error = Record.Exception(() => InvestigationAdviceContract.ParseAndValidate(InvestigationAdviceContract.Serialize(advice), evidence));
        if (accepted)
        {
            Assert.Null(error);
        }
        else
        {
            Assert.Equal("advice-readiness", Assert.IsType<LlmException>(error).ValidationRule);
        }
    }

    [Theory]
    [InlineData(0, 7000, 32000, true)]
    [InlineData(3, 8250, 32000, true)]
    [InlineData(39, 12500, 32000, true)]
    [InlineData(39, 12500, 8000, false)]
    public async Task ScopedSchemaRespectsExistingEngineBudget(int currentPoints, int schemaLimit, int inputBudget, bool fitsBudget)
    {
        var point = Guid.NewGuid();
        var evidence = Enumerable.Range(1, 40).Select(i =>
        {
            // The production service captures the alarm point and at most two related points.
            // The 39-point case exercises a larger synthetic scope at the same ledger limit.
            if (i == 1 || i > currentPoints + 1)
            {
                return new InvestigationEvidence("observation-" + i, "Alarm", "Snapshot", ["Observation"], "h", DateTimeOffset.UtcNow, point);
            }
            var current = Guid.NewGuid();
            return new InvestigationEvidence("observation-" + i, "PointStatus", "Snapshot", ["Observation"], "h", DateTimeOffset.UtcNow, current,
                PointStatus: new()
                {
                    PointId = current,
                    CurrentTrust = "Trusted",
                    CurrentAnomaly = i == 2 ? "Present" : "NotFound"
                });
        }).ToArray();
        var schema = InvestigationAdviceContract.ForModel(evidence).Schema;
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.False(schema.TryGetProperty("anyOf", out _));
        foreach (var keyword in new[] { "allOf", "if", "then", "else", "not", "dependentSchemas" })
        {
            Assert.Empty(FindFields(JsonNode.Parse(schema.GetRawText())!, keyword));
        }
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(schema.GetRawText()) < schemaLimit);
        var advice = InvestigationAdviceTests.Valid(new InvestigationModelSubjects(evidence).AliasFor(point)!, evidence[0].Id);
        advice = advice with
        {
            Findings = [advice.Findings[0] with { Scope = "AlarmEvent" }]
        };
        using var budget = new LlmBudget(new(MaxInputTokens: inputBudget, MaxOutputTokens: 6000));
        var engine = new InvestigationEngine(
            new ScriptedLlmGateway([new(InvestigationAdviceContract.Serialize(advice), [], "script", new(100, 100))]),
            Options.Create(new AiOptions()));
        Task<InvestigationExecutionResult> Execute() => engine.ExecuteAsync(
            evidence, (_, _, _) => throw new InvalidOperationException(), _ => Task.CompletedTask, budget, CancellationToken.None);
        if (!fitsBudget)
        {
            var error = await Assert.ThrowsAsync<LlmException>(Execute);
            Assert.Equal(LlmFailure.BudgetExceeded, error.Failure);
            Assert.Equal(0, budget.ModelRounds);
            return;
        }
        var result = await Execute();
        Assert.NotNull(result.ResultJson);
        Assert.Equal(1, budget.ModelRounds);
    }

    private static IEnumerable<JsonNode> FindFields(JsonNode node, string name)
    {
        if (node is JsonObject obj)
        {
            foreach (var child in obj)
            {
                if (child.Key == name && child.Value != null)
                {
                    yield return child.Value;
                }
                if (child.Value != null)
                {
                    foreach (var found in FindFields(child.Value, name))
                    {
                        yield return found;
                    }
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array.OfType<JsonNode>())
            {
                foreach (var found in FindFields(child, name))
                {
                    yield return found;
                }
            }
        }
    }

    private static bool Accepts(JsonElement value, JsonNode node, JsonElement root)
    {
        JsonNode Expand(JsonNode source)
        {
            if (source is JsonObject obj)
            {
                if (obj["$ref"] is JsonValue reference)
                {
                    return Expand(JsonNode.Parse(root.GetProperty("$defs").GetProperty(reference.GetValue<string>().Split('/')[^1]).GetRawText())!);
                }
                return new JsonObject(obj.Where(x => x.Key != "$defs").Select(x => KeyValuePair.Create(x.Key, x.Value == null ? null : Expand(x.Value))));
            }
            return source is JsonArray array ? new JsonArray(array.Select(x => x == null ? null : Expand(x)).ToArray()) : source.DeepClone();
        }
        var expanded = JsonSerializer.SerializeToElement(Expand(node));
        try
        {
            typeof(InvestigationAdviceContract).GetMethod("ValidateShape", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [value, expanded, "$"]);
            return true;
        }
        catch (TargetInvocationException e) when (e.InnerException is JsonException)
        {
            return false;
        }
    }

    private static bool ModelAccepts(InvestigationAdvice advice, IReadOnlyList<InvestigationEvidence> evidence)
    {
        var subjects = new InvestigationModelSubjects(evidence);
        string[] Aliases(string[] refs) => refs.Select(x => subjects.AliasFor(Guid.Parse(x)) ?? x).ToArray();
        advice = advice with
        {
            Findings = advice.Findings.Select(x => x with { SubjectRefs = Aliases(x.SubjectRefs) }).ToArray(),
            Recommendations = advice.Recommendations.Select(x => x with { SubjectRefs = Aliases(x.SubjectRefs) }).ToArray()
        };
        var schema = InvestigationAdviceContract.ForModel(evidence, subjects).Schema;
        using var result = JsonDocument.Parse(InvestigationAdviceContract.Serialize(advice));
        return Accepts(result.RootElement, JsonNode.Parse(schema.GetRawText())!, schema);
    }
}

