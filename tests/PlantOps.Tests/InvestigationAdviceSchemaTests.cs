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
        var error = Assert.Throws<LlmException>(() => InvestigationAdviceContract.ParseAndValidate(InvestigationAdviceContract.Serialize(invalid), evidence));
        Assert.Equal("advice-current-evidence", error.ValidationRule);
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
        Assert.True(Accepts(JsonSerializer.SerializeToElement(advice, new JsonSerializerOptions(JsonSerializerDefaults.Web)), JsonNode.Parse(schema.GetRawText())!, schema));
        Assert.Equal(gapId, Assert.Single(InvestigationAdviceContract.ParseAndValidate(InvestigationAdviceContract.Serialize(advice), evidence).BlockingGapIds));
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

    [Fact]
    public async Task ScopedSchemaRemainsCompactAndFitsExistingEngineBudget()
    {
        var point = Guid.NewGuid();
        var evidence = Enumerable.Range(1, 40).Select(i => new InvestigationEvidence("observation-" + i, "Alarm", "Snapshot", ["Observation"], "h", DateTimeOffset.UtcNow, point)).ToArray();
        var schema = InvestigationAdviceContract.ForModel(evidence).Schema;
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.False(schema.TryGetProperty("anyOf", out _));
        foreach (var keyword in new[] { "allOf", "if", "then", "else", "not", "dependentSchemas" })
        {
            Assert.Empty(FindFields(JsonNode.Parse(schema.GetRawText())!, keyword));
        }
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(schema.GetRawText()) < 6500);
        var advice = Advice(point, evidence[0].Id);
        using var budget = new LlmBudget(new(MaxInputTokens: 32000, MaxOutputTokens: 6000));
        var result = await new InvestigationEngine(new ScriptedLlmGateway([new(InvestigationAdviceContract.Serialize(advice), [], "script", new(100, 100))]), Options.Create(new AiOptions()))
            .ExecuteAsync(evidence, (_, _, _) => throw new InvalidOperationException(), _ => Task.CompletedTask, budget, CancellationToken.None);
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
}

