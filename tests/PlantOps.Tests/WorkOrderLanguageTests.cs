using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PlantOps.AI;
using PlantOps.Application.AI;
using PlantOps.Infrastructure.AI;
using Xunit;

namespace PlantOps.Tests;

public sealed class WorkOrderLanguageTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("zh-CN")]
    public async Task SimulatedInvestigationCarriesItsLanguageIntoWorkOrderAndNoteDrafts(string language)
    {
        var evidence = new InvestigationEvidence("E1", "Alarm", "所选告警的历史快照",
            ["触发值为 12.6 mm/s，触发时间为 2026-10-27T03:17:17Z；这是历史告警快照。",
             "调查取数时告警状态为 Recovered，恢复时间为 未记录，人工确认状态为 未确认；确认不代表恢复，恢复不代表根因已消除。"],
            new string('a', 64), DateTimeOffset.UtcNow, Guid.NewGuid());
        using var budget = new LlmBudget(new(MaxInputTokens: 32000, MaxOutputTokens: 6000));
        var output = await new InvestigationEngine(new FakeLlmGateway(), Options.Create(new AiOptions { ServerRenderedFacts = true }))
            .ExecuteAsync([evidence], (_, _, _) => throw new InvalidOperationException(), _ => Task.CompletedTask, budget, CancellationToken.None, language);
        using var json = JsonDocument.Parse(output.ResultJson);
        var run = new InvestigationRunDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Succeeded", DateTimeOffset.UtcNow,
            null, null, "Fake", "fake", true, null, json.RootElement.Clone(), [evidence], [], 1, 0, 0, 0, Language: language);
        var template = typeof(ActionProposalService).GetMethod("Template", BindingFlags.Static | BindingFlags.NonPublic)!;
        var draft = (WorkOrderDraft)template.Invoke(null, [run])!;
        Assert.Equal(json.RootElement.GetProperty("workOrder").GetProperty("draft").GetProperty("title").GetString(), draft.Title);
        Assert.Contains(json.RootElement.GetProperty("recommendations")[0].GetProperty("task").GetString()!, draft.Description);
        Assert.Contains("[E1]", draft.Description);
        if (language == "en")
        {
            Assert.DoesNotMatch(@"\p{IsCJKUnifiedIdeographs}", draft.Description);
        }
        else
        {
            Assert.Contains("人工审核草稿", draft.Description);
        }

        Assert.Equal("Medium", draft.Priority);
        Assert.StartsWith("触发值为", evidence.Facts[0], StringComparison.Ordinal);
    }
}
