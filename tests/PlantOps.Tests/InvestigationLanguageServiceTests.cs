using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PlantOps.Application.AI;
using PlantOps.Infrastructure.Persistence;
using Xunit;

namespace PlantOps.Tests;

[Collection("Database")]
public sealed class InvestigationLanguageServiceTests
{
    [DatabaseFact]
    public async Task LanguageSurvivesQueueProcessingAndCannotBeChangedByReusingAnActiveRun()
    {
        var clock = new AlarmTests.Clock();
        await using var session = new AssetManagementTests.Session(clock, await TestDatabase.IsolatedConnection());
        await session.Initialize();
        await session.Login();
        var configuration = session.Factory.Services.GetRequiredService<IConfiguration>();
        configuration["AI:ServerRenderedFacts"] = "true";
        configuration["AI:MaxInputTokens"] = "32000";
        configuration["AI:MaxOutputTokens"] = "6000";
        var fixture = await AlarmTests.Configure(session);
        var alarm = await AlarmTests.Raise(session, fixture, clock);
        var path = $"/api/v1/alarms/{alarm.Id}/investigations";
        var response = await session.Client.PostAsync(path + "?language=en", null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var run = (await response.Content.ReadFromJsonAsync<InvestigationRunDto>())!;
        Assert.Equal("en", run.Language);
        var repeated = (await (await session.Client.PostAsync(path + "?language=zh-CN", null)).Content.ReadFromJsonAsync<InvestigationRunDto>())!;
        Assert.Equal(run.Id, repeated.Id);
        Assert.Equal("en", repeated.Language);
        await using (var scope = session.Factory.Services.CreateAsyncScope())
        {
            var stored = await scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>().AiRuns.AsNoTracking().SingleAsync(x => x.Id == run.Id);
            Assert.Equal("en", stored.Language);
            Assert.True(await scope.ServiceProvider.GetRequiredService<IInvestigationService>().ProcessNextAsync());
        }
        var finished = (await session.Client.GetFromJsonAsync<InvestigationRunDto>($"/api/v1/investigations/{run.Id}"))!;
        Assert.Equal("Succeeded", finished.Status);
        Assert.Equal("en", finished.Language);
        Assert.Equal("en", finished.RunRecord!.Value.GetProperty("language").GetString());
        Assert.Equal("en", Assert.Single(finished.RunRecord.Value.GetProperty("requests").EnumerateArray()).GetProperty("language").GetString());
        Assert.StartsWith("Review", finished.Result!.Value.GetProperty("nextChecks")[0].GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Contains(finished.Evidence, item => item.Facts.Any(fact => fact.Contains("告警", StringComparison.Ordinal)));
        var next = (await (await session.Client.PostAsync(path, null)).Content.ReadFromJsonAsync<InvestigationRunDto>())!;
        Assert.NotEqual(run.Id, next.Id);
        Assert.Equal("zh-CN", next.Language);
    }

    [DatabaseFact]
    public async Task UnsupportedLanguageIsRejectedWithoutCreatingATask()
    {
        await using var session = new AssetManagementTests.Session(connection: await TestDatabase.IsolatedConnection());
        await session.Initialize();
        await session.Login();
        var response = await session.Client.PostAsync($"/api/v1/alarms/{Guid.NewGuid()}/investigations?language=French", null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var scope = session.Factory.Services.CreateAsyncScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>().AiRuns.ToListAsync());
    }
}
