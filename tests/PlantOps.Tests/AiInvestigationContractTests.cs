using System.Text.Json.Nodes;
using PlantOps.AI;
using PlantOps.Application.AI;
using Xunit;

namespace PlantOps.Tests;

public sealed class AiInvestigationContractTests
{
    private static readonly HashSet<string> Allowed = new(["E1"], StringComparer.Ordinal);

    private static string Valid() => InvestigationContract.Serialize(new("1", "Synthetic observation; cause unknown.",
        [new("12.6 mm/s", ["E1"])], [], [], ["No field inspection."], [], ["E1"]));

    [Fact]
    public void AcceptsValidShapeAndExplicitAuthorizedReferences()
    {
        var result = InvestigationContract.ParseAndValidateReferences(Valid(), Allowed);
        Assert.Equal("1", result.SchemaVersion);
        Assert.Equal("E1", Assert.Single(Assert.Single(result.Observations).EvidenceIds));
    }

    [Theory]
    [InlineData("schemaVersion")]
    [InlineData("summary")]
    [InlineData("observations")]
    [InlineData("hypotheses")]
    [InlineData("nextChecks")]
    [InlineData("missingInformation")]
    [InlineData("dataWarnings")]
    [InlineData("evidenceIds")]
    public void RejectsMissingRequiredFields(string field)
    {
        var json = JsonNode.Parse(Valid())!.AsObject();
        json.Remove(field);
        AssertInvalid(json.ToJsonString());
    }

    [Fact]
    public void RejectsUnknownFieldsIncludingModelGeneratedLinks()
    {
        var json = JsonNode.Parse(Valid())!.AsObject();
        json["downloadUrl"] = "https://attacker.invalid/";
        AssertInvalid(json.ToJsonString());
    }

    [Fact]
    public void RejectsUnauthorizedReferencesAndFactsWithoutReferences()
    {
        AssertInvalid(Valid().Replace("E1", "OtherPlantEvidence", StringComparison.Ordinal));
        var json = JsonNode.Parse(Valid())!.AsObject();
        json["observations"]![0]!["evidenceIds"] = new JsonArray();
        AssertInvalid(json.ToJsonString());
    }

    [Fact]
    public void RejectsMissingTopLevelReferencesAndDuplicateReferences()
    {
        var json = JsonNode.Parse(Valid())!.AsObject();
        json["evidenceIds"] = new JsonArray();
        AssertInvalid(json.ToJsonString());
        json["evidenceIds"] = new JsonArray("E1", "E1");
        AssertInvalid(json.ToJsonString());
    }

    [Fact]
    public void RejectsProviderWarningsBecauseServerOwnsWarnings()
    {
        var json = JsonNode.Parse(Valid())!.AsObject();
        json["dataWarnings"] = new JsonArray("All data is safe.");
        AssertInvalid(json.ToJsonString());
    }

    [Fact]
    public void RejectsNullsAndLongText()
    {
        var json = JsonNode.Parse(Valid())!.AsObject();
        json["observations"] = null;
        AssertInvalid(json.ToJsonString());
        json = JsonNode.Parse(Valid())!.AsObject();
        json["summary"] = new string('x', 2001);
        AssertInvalid(json.ToJsonString());
    }

    [Fact]
    public void RejectsBadHypothesisSupportAndUnprovenHypothesis()
    {
        var result = new InvestigationResult("1", "Unknown cause.", [],
            [new("Loose mount may be involved.", "certain", ["E1"], "Inspect using the approved procedure.")], [], [], [], ["E1"]);
        AssertInvalid(InvestigationContract.Serialize(result));
        result = result with
        {
            Hypotheses = [new("Loose mount may be involved.", "weak", [], "Inspect using the approved procedure.")]
        };
        AssertInvalid(InvestigationContract.Serialize(result));
    }

    private static void AssertInvalid(string content) => Assert.Equal(LlmFailure.InvalidResponse,
        Assert.Throws<LlmException>(() => InvestigationContract.ParseAndValidateReferences(content, Allowed)).Failure);
}
