using System.Text.Json;
using Microsoft.Extensions.Options;
using PlantOps.AI;
using PlantOps.Application.AI;
using Xunit;

namespace PlantOps.Tests;

// Historical v1/v2 language validation and replay.
public sealed class LegacyInvestigationLanguageTests
{
    private const string Fact = "触发值为 12.6 mm/s；这是历史告警快照。";
    private static readonly InvestigationEvidence Evidence = new("E1", "Alarm", "原始告警", [Fact], new string('a', 64), DateTimeOffset.UtcNow, Guid.NewGuid());
    private static readonly InvestigationCheck Check = new("Review the event evidence.", ["E1"],
        "Record the evidence and any gaps.", "Complete once all available evidence is documented and reviewed.",
        "Refer unresolved uncertainty to a maintenance engineer.");

    private static InvestigationResult English() => new("1", LegacyInvestigationEngine.EnglishSafeSummary,
        [new(Fact, ["E1"])], [], [Check], [], [], ["E1"]);

    [Theory]
    [InlineData(false, "en")]
    [InlineData(true, "en")]
    [InlineData(false, "zh-CN")]
    [InlineData(true, "zh-CN")]
    public async Task RequestedLanguageControlsThePromptAndAdviceButNeverRewritesSourceEvidence(bool serverFacts, string language)
    {
        var gateway = new InspectingGateway();
        using var budget = new LlmBudget(new(MaxInputTokens: 32000, MaxOutputTokens: 6000));
        var result = await new LegacyInvestigationEngine(gateway, Options.Create(new AiOptions { ServerRenderedFacts = serverFacts }))
            .ExecuteAsync([Evidence], (_, _, _) => throw new InvalidOperationException(), _ => Task.CompletedTask,
                budget, CancellationToken.None, language);
        var parsed = LegacyInvestigationEngine.ValidateFacts(result.ResultJson, [Evidence], language: language);
        Assert.Equal(Fact, Assert.Single(parsed.Observations).Text);
        Assert.Equal(language, Assert.Single(budget.Requests).GetProperty("language").GetString());
        using var input = JsonDocument.Parse(gateway.Request!.Messages.Single(x => x.Role == LlmRole.User).Content!);
        Assert.Equal(language, input.RootElement.GetProperty("language").GetString());
        if (language == "en")
        {
            Assert.Contains("Respond in English", gateway.Request.Messages[0].Content, StringComparison.Ordinal);
            Assert.DoesNotContain("Chinese.", gateway.Request.Messages[0].Content, StringComparison.Ordinal);
            Assert.Equal(LegacyInvestigationEngine.EnglishSafeSummary, parsed.Summary);
            Assert.StartsWith("Review", Assert.Single(parsed.NextChecks).Text, StringComparison.Ordinal);
            Assert.DoesNotMatch(@"\p{IsCJKUnifiedIdeographs}", parsed.MissingInformation.Single());
        }
        else
        {
            Assert.Contains("Chinese.", gateway.Request.Messages[0].Content, StringComparison.Ordinal);
            Assert.StartsWith("核对", Assert.Single(parsed.NextChecks).Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EnglishAdviceAllowsOnlyWholeCitedOriginalFactsAndTentativeHypotheses()
    {
        var result = English() with
        {
            Hypotheses = [new("Possible operating-condition influence.", "weak", ["E1"],
                "Review the contemporaneous operating records.", Fact, "Operating conditions are not documented.")],
            NextChecks = [Check with { Text = "Review this original fact: " + Fact }]
        };
        Assert.NotNull(LegacyInvestigationEngine.ValidateFacts(InvestigationContract.Serialize(result), [Evidence], language: "en"));
        Assert.Throws<LlmException>(() => LegacyInvestigationEngine.ValidateFacts(InvestigationContract.Serialize(result with
        {
            Observations = [new("Trigger value was 12.6 mm/s.", ["E1"])]
        }), [Evidence], language: "en"));
    }

    [Theory]
    [InlineData("Review value 12.6.")]
    [InlineData("核对事件证据。")]
    public void LegacyEnglishPreservesNumericGroundingAndResponseLanguage(string text)
    {
        var result = English() with
        {
            NextChecks = [Check with { Text = text }]
        };
        Assert.Throws<LlmException>(() => LegacyInvestigationEngine.ValidateFacts(InvestigationContract.Serialize(result), [Evidence], language: "en"));
    }

    [Fact]
    public void EnglishMissingDataClaimsAreCheckedAgainstTheFullLedger()
    {
        var raw = Evidence with
        {
            Id = "E2",
            Kind = "EventReadings",
            Facts = ["事件原始观测已入库。"],
            State = new("Partial")
        };
        var maintenance = Evidence with
        {
            Id = "E3",
            Kind = "Maintenance",
            Facts = ["维护记录：已记录检查结果。"]
        };
        foreach (var claim in new[] { "Raw observations are missing.", "No maintenance records." })
        {
            Assert.Throws<LlmException>(() => LegacyInvestigationEngine.ValidateFacts(InvestigationContract.Serialize(English() with
            {
                MissingInformation = [claim],
                StateClaims = [new("E2", "Empty", null, null)]
            }), [Evidence, raw, maintenance], language: "en"));
        }
        var conditional = English() with
        {
            NextChecks = [Check with { Escalation = "If raw observations are missing, refer the evidence gap to a maintenance engineer." }]
        };
        Assert.NotNull(LegacyInvestigationEngine.ValidateFacts(InvestigationContract.Serialize(conditional), [Evidence, raw], language: "en"));
        var falseConsequence = English() with
        {
            NextChecks = [Check with { Escalation = "If review is needed, raw observations are missing." }],
            StateClaims = [new("E2", "Empty", null, null)]
        };
        Assert.Throws<LlmException>(() => LegacyInvestigationEngine.ValidateFacts(InvestigationContract.Serialize(falseConsequence), [Evidence, raw], language: "en"));
    }

    private sealed class InspectingGateway : ILlmGateway
    {
        public LlmRequest? Request
        {
            get; private set;
        }

        public Task<LlmCompletion> CompleteAsync(LlmRequest request, LlmBudget budget, CancellationToken cancellationToken = default)
        {
            Request = request;
            return new FakeLlmGateway().CompleteAsync(request, budget, cancellationToken);
        }
    }
}
