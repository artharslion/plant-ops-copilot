using System.Text.Json;
using Microsoft.Extensions.Options;
using PlantOps.AI;
using PlantOps.Application.AI;
using Xunit;

namespace PlantOps.Tests;

public sealed class InvestigationStateClaimsTests
{
    private static readonly InvestigationEvidence Evidence = new("E1", "EventReadings", "Excerpt",
        ["Bounded observations retained."], "hash", DateTimeOffset.UtcNow, Guid.NewGuid(), State: new("Partial"));

    private static InvestigationResult Answer(string prose = "Review the available excerpt.") => new("1",
        InvestigationEngine.EnglishSafeSummary, [], [],
        [new(prose, ["E1"], "Record coverage and limitations.", "Complete after maintenance review.",
            "If complete history is unavailable, refer retention limits to the maintenance engineer.")], [], [],
        StateClaims: [new("E1", "Partial", null, null)]);

    [Theory]
    [InlineData("Review the available excerpt.")]
    [InlineData("If additional raw observations are missing, document the remaining scope.")]
    [InlineData("No maintenance records are needed for this review.")]
    public void EquivalentStateDoesNotDependOnProseOrEvidenceWording(string prose)
    {
        foreach (var fact in new[] { "Bounded observations retained.", "事件原始观测不可查询。", "Renamed source presentation." })
        {
            var result = InvestigationEngine.ValidateFacts(InvestigationContract.Serialize(Answer(prose)),
                [Evidence with { Facts = [fact] }], language: "en");
            Assert.Equal("Partial", Assert.Single(result.StateClaims!).Availability);
        }
    }

    [Theory]
    [InlineData("Empty")]
    [InlineData("Unavailable")]
    [InlineData("Available")]
    public void PlausibleProseCannotOverrideServerAvailability(string availability)
    {
        var answer = Answer() with
        {
            StateClaims = [new("E1", availability, null, null)]
        };
        var error = Assert.Throws<LlmException>(() => InvestigationEngine.ValidateFacts(
            InvestigationContract.Serialize(answer), [Evidence], language: "en"));
        Assert.Equal("contradictory-state-claim", error.ValidationRule);
    }

    [Theory]
    [InlineData("FOREIGN", "Partial")]
    [InlineData("E1", "Invented")]
    public void ClaimsRequireAuthorizedReferencesAndKnownEnums(string id, string availability)
    {
        var answer = Answer() with
        {
            StateClaims = [new(id, availability, null, null)]
        };
        Assert.Throws<LlmException>(() => InvestigationEngine.ValidateFacts(InvestigationContract.Serialize(answer), [Evidence]));
    }

    [Fact]
    public void DuplicateClaimsAreRejected()
    {
        var answer = Answer() with
        {
            StateClaims = [new("E1", "Partial", null, null), new("E1", "Partial", null, null)]
        };
        Assert.Throws<LlmException>(() => InvestigationEngine.ValidateFacts(InvestigationContract.Serialize(answer), [Evidence]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProviderSchemaRequiresStatesWithoutChangingStoredLegacyContract(bool compact)
    {
        var schema = InvestigationEngine.ModelSchema(InvestigationContract.LegacyOutputSchema, compact, true,
            InvestigationEngine.EnglishSafeSummary, structuredStates: true).Schema;
        Assert.Contains(schema.GetProperty("required").EnumerateArray(), x => x.GetString() == "stateClaims");
        Assert.True(schema.GetProperty("properties").TryGetProperty("stateClaims", out _));
        Assert.False(InvestigationContract.LegacyOutputSchema.Schema.GetProperty("properties").TryGetProperty("stateClaims", out _));
        Assert.NotNull(InvestigationEngine.ValidateFacts(InvestigationContract.Serialize(Answer() with
        {
            StateClaims = null
        }), [Evidence], language: "en"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewRunsRequireClaimsEvenWhenLegacyResultsRemainReadable(bool emptyClaims)
    {
        var answer = Answer() with
        {
            StateClaims = emptyClaims ? [] : null
        };
        using var budget = new LlmBudget(new(MaxInputTokens: 32000, MaxOutputTokens: 6000));
        var gateway = new FakeLlmGateway([new(InvestigationContract.Serialize(answer), [], "fake", new(100, 20))]);
        var error = await Assert.ThrowsAsync<LlmException>(() => new LegacyInvestigationEngine(gateway, Options.Create(new AiOptions()))
            .ExecuteAsync([Evidence], (_, _, _) => throw new InvalidOperationException(), _ => Task.CompletedTask,
                budget, CancellationToken.None, "en"));
        Assert.Equal("missing-state-claims", error.ValidationRule);
    }

    [Fact]
    public async Task NewRunsExposeStatesAndPersistValidatedClaims()
    {
        using var budget = new LlmBudget(new(MaxInputTokens: 32000, MaxOutputTokens: 6000));
        var gateway = new FakeLlmGateway([new(InvestigationContract.Serialize(Answer()), [], "fake", new(100, 20))]);
        var result = await new LegacyInvestigationEngine(gateway, Options.Create(new AiOptions())).ExecuteAsync([Evidence],
            (_, _, _) => throw new InvalidOperationException(), _ => Task.CompletedTask, budget, CancellationToken.None, "en");
        using var stored = JsonDocument.Parse(result.ResultJson);
        Assert.Equal("Partial", stored.RootElement.GetProperty("stateClaims")[0].GetProperty("availability").GetString());
        Assert.Contains("E1", stored.RootElement.GetProperty("evidenceIds").EnumerateArray().Select(x => x.GetString()));
    }
}
