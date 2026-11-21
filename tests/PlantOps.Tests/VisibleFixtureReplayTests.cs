using System.Security.Cryptography;
using System.Text.Json;
using PlantOps.AI;
using PlantOps.Application.AI;
using Xunit;

namespace PlantOps.Tests;

public sealed class VisibleFixtureReplayTests
{
    internal const string RootVariable = "PLANTOPS_VISIBLE_REPLAY_ROOT";

    [VisibleFixtureTheory]
    [InlineData("trusted-stable")]
    [InlineData("sustained-heating-zh-cn")]
    [InlineData("sustained-heating-en")]
    [InlineData("partial-bad-high-vibration")]
    [InlineData("all-unverifiable")]
    [InlineData("disconnected")]
    [InlineData("published-manual")]
    [InlineData("withdrawn-manual")]
    public async Task CurrentPolicyAndContractAcceptFrozenFixture(string scenario)
    {
        var root = Environment.GetEnvironmentVariable(RootVariable)!;
        var path = Path.Combine(root, scenario + ".json");
        await using var input = File.OpenRead(path);
        using var file = await JsonDocument.ParseAsync(input);
        var fixture = file.RootElement;
        Assert.Equal(1, fixture.GetProperty("formatVersion").GetInt32());
        Assert.Equal(scenario, fixture.GetProperty("scenario").GetString());
        Assert.True(fixture.GetProperty("fixtureOnly").GetBoolean());
        Assert.False(fixture.GetProperty("independentHoldout").GetBoolean());
        Assert.False(fixture.GetProperty("protocolObservation").GetBoolean());
        Assert.Equal("NotPerformed", fixture.GetProperty("semanticAssessment").GetString());

        var json = JsonSerializerOptions.Web;
        var run = fixture.GetProperty("run").Deserialize<InvestigationRunDto>(json)!;
        Assert.Equal("Fake", run.Provider);
        Assert.True(run.Simulated);
        Assert.Equal("Succeeded", run.Status);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(run.Evidence, json);
        Assert.Equal(run.RunRecord!.Value.GetProperty("evidenceSha256").GetString(),
            Convert.ToHexStringLower(SHA256.HashData(bytes)));

        var assessment = InvestigationAssessmentPolicy.FromEvidence(run.Evidence);
        Assert.NotNull(assessment);
        foreach (var point in assessment.Points)
        {
            var current = InvestigationAssessmentPolicy.Evaluate(point);
            Assert.Equal(point.CurrentTrust, current.CurrentTrust);
            Assert.Equal(point.CurrentAnomaly, current.CurrentAnomaly);
        }

        var expected = fixture.GetProperty("expectedDeterministicAssessment");
        Assert.Equal(expected.GetProperty("dataTrust").GetString(), assessment.DataTrust);
        Assert.Equal(expected.GetProperty("anomalyEvidence").GetString(), assessment.AnomalyEvidence);
        InvestigationAdviceContract.ParseAndValidate(
            JsonSerializer.Serialize(run.Result!.Value, json), run.Evidence, run.Language);
    }
}

public sealed class VisibleFixtureTheoryAttribute : TheoryAttribute
{
    public VisibleFixtureTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(VisibleFixtureReplayTests.RootVariable)))
        {
            Skip = "Requires an explicitly selected directory of saved visible engineering fixtures.";
        }
    }
}
