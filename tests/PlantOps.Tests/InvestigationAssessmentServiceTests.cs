using System.Text.Json;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PlantOps.Application.AI;
using PlantOps.Application.Telemetry;
using PlantOps.Infrastructure.Persistence;
using Xunit;

namespace PlantOps.Tests;

[Collection("Database")]
public sealed class InvestigationAssessmentServiceTests
{
    private static async Task<InvestigationRunDto> Investigate(AssetManagementTests.Session session, Guid alarm)
    {
        var created = await session.Client.PostAsync($"/api/v1/alarms/{alarm}/investigations", null);
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        var run = (await created.Content.ReadFromJsonAsync<InvestigationRunDto>())!;
        await using var scope = session.Factory.Services.CreateAsyncScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<IInvestigationService>().ProcessNextAsync());
        run = (await session.Client.GetFromJsonAsync<InvestigationRunDto>($"/api/v1/investigations/{run.Id}"))!;
        Assert.True(run.Status == "Succeeded", run.Status + ":" + run.FailureCode);
        return run;
    }

    [DatabaseFact]
    public async Task BadTemperatureAndFreshHighVibrationPersistPartialPresentAndDraftWithoutBusinessExecution()
    {
        var clock = new AlarmTests.Clock();
        await using var session = new AssetManagementTests.Session(clock, await TestDatabase.IsolatedConnection());
        await session.Initialize();
        await session.Login();
        var fixture = await AlarmTests.Configure(session);
        var alarm = await AlarmTests.Raise(session, fixture, clock);
        var config = session.Factory.Services.GetRequiredService<IConfiguration>();
        config["AI:MaxInputTokens"] = "32000";
        config["AI:MaxOutputTokens"] = "6000";
        config["AI:ServerRenderedFacts"] = "true";
        var response = await session.Client.PostAsJsonAsync($"/api/v1/assets/{fixture.Asset.Id}/measurement-points",
            new PointInput("TEMPERATURE", "温度", "Temperature", "degC"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var point = (await response.Content.ReadFromJsonAsync<PointOverview>())!;
        config["OpcUa:LocalRuntimeEnabled"] = "true";
        const string prefix = "OpcUa:ApprovedEndpoints:local-runtime:";
        config[prefix + "Url"] = OpcUaRuntimeTests.Endpoint.Url;
        config[prefix + "ApplicationUri"] = OpcUaRuntimeTests.Endpoint.ApplicationUri;
        config[prefix + "NamespaceUri"] = OpcUaRuntimeTests.Endpoint.NamespaceUri;
        config[prefix + "CertificateRoot"] = OpcUaRuntimeTests.Endpoint.CertificateRoot;
        config[prefix + "Nodes:0"] = "String:plantops-p101/temperature";
        var sourceResponse = await session.Client.PostAsJsonAsync($"/api/v1/plants/{fixture.Asset.PlantId}/data-sources",
            new SourceInput("Controlled temperature fixture", Kind: "OpcUa", OpcUa: new("local-runtime")));
        Assert.Equal(HttpStatusCode.Created, sourceResponse.StatusCode);
        var source = (await sourceResponse.Content.ReadFromJsonAsync<SourceDto>())!;
        var mappingResponse = await session.Client.PostAsJsonAsync($"/api/v1/assets/{fixture.Asset.Id}/measurement-points/{point.Id}/source-mappings",
            new MappingInput(source.Id, "temperature", RawUnit: "degC", OpcUa: new(OpcUaRuntimeTests.Endpoint.NamespaceUri, "String", "plantops-p101/temperature")));
        Assert.Equal(HttpStatusCode.Created, mappingResponse.StatusCode);
        await using var scope = session.Factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var tempConfig = (await services.GetRequiredService<ISourceConfigurationReader>().GetEnabledAsync()).Single(x => x.PointId == point.Id);
        await services.GetRequiredService<ISourceRuntimeStore>().ActivateAsync([tempConfig],
            [new(point.Id, tempConfig.MappingId, tempConfig.MappingVersion, clock.Now, "Periodic", 1000, 1000, 10)], default);
        await services.GetRequiredService<IMeasurementIngestor>().IngestAsync([
            SamplePolicy.Normalize(tempConfig, 90, "Bad", clock.Now, null, clock.Now, "bad-temperature")]);
        var db = services.GetRequiredService<PlantOpsDbContext>();
        var before = await db.WorkOrders.CountAsync();
        var run = await Investigate(session, alarm.Id);
        var assessment = run.Assessment!;
        Assert.Equal("Partial", assessment.DataTrust);
        Assert.Equal("Present", assessment.AnomalyEvidence);
        var temperature = Assert.Single(assessment.Points, x => x.PointId == point.Id);
        Assert.Equal("Bad", temperature.Observation!.Quality);
        Assert.Equal("Unverifiable", temperature.CurrentTrust);
        Assert.Equal("Present", Assert.Single(assessment.Points, x => x.PointId == fixture.Point.Id).CurrentAnomaly);
        Assert.All(assessment.EvidenceIds, id => Assert.Contains(run.Evidence, x => x.Id == id && x.PointStatus != null));
        foreach (var item in run.Evidence.Where(x => x.PointStatus != null))
        {
            var expanded = (await session.Client.GetFromJsonAsync<InvestigationEvidence>($"/api/v1/investigations/{run.Id}/evidence/{item.Id}"))!;
            Assert.Equal(item.Sha256, expanded.Sha256);
            Assert.Equal(item.PointStatus!.Observation!.Id, expanded.PointStatus!.Observation!.Id);
        }
        var proposalResponse = await session.Client.PostAsync($"/api/v1/investigations/{run.Id}/proposals", null);
        Assert.True(proposalResponse.IsSuccessStatusCode, await proposalResponse.Content.ReadAsStringAsync());
        var proposal = (await proposalResponse.Content.ReadFromJsonAsync<ActionProposalDto>())!;
        var advice = run.Result!.Value.Deserialize<InvestigationAdvice>(new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        Assert.Contains(advice.Findings, x => x.Status == "AnomalySupported" && x.SubjectRefs.Contains(fixture.Point.Id.ToString()));
        Assert.Contains(advice.Findings, x => x.Status == "Inconclusive" && x.SubjectRefs.Contains(point.Id.ToString()));
        Assert.Contains(advice.Recommendations[0].Task, proposal.Draft!.Description);
        Assert.Equal(before, await db.WorkOrders.CountAsync());
        var frozenAsOf = assessment.AsOf;
        clock.Now = clock.Now.AddHours(1);
        var reread = (await session.Client.GetFromJsonAsync<InvestigationRunDto>($"/api/v1/investigations/{run.Id}"))!;
        Assert.Equal(frozenAsOf, reread.Assessment!.AsOf);
        Assert.Equal("Present", reread.Assessment.AnomalyEvidence);
    }

    [DatabaseScenarioTheory]
    [InlineData("Fresh", "Trusted", "Present")]
    [InlineData("Stale", "Unverifiable", "Insufficient")]
    [InlineData("SourceDisabled", "Unverifiable", "Insufficient")]
    [InlineData("MappingChanged", "Unverifiable", "Insufficient")]
    [InlineData("SourceVersionChanged", "Unverifiable", "Insufficient")]
    [InlineData("PointVersionChanged", "Unverifiable", "Insufficient")]
    [InlineData("LegacyVersions", "Unverifiable", "Insufficient")]
    [InlineData("Recovered", "Trusted", "NotFound")]
    public async Task CurrentAssessmentSeparatesHistoricalAlarmFromFrozenCurrentInputs(string scenario, string trust, string anomaly)
    {
        var clock = new AlarmTests.Clock();
        await using var session = new AssetManagementTests.Session(clock, await TestDatabase.IsolatedConnection());
        await session.Initialize();
        await session.Login();
        var fixture = await AlarmTests.Configure(session);
        var alarm = await AlarmTests.Raise(session, fixture, clock);
        await using var scope = session.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
        if (scenario == "Stale")
        {
            clock.Now = clock.Now.AddMinutes(1);
        }
        if (scenario == "SourceDisabled")
        {
            await db.DataSources.Where(x => x.Id == fixture.Config.DataSourceId).ExecuteUpdateAsync(x => x.SetProperty(s => s.Enabled, false));
        }
        if (scenario == "MappingChanged")
        {
            await db.SourceMappings.Where(x => x.Id == fixture.Config.MappingId).ExecuteUpdateAsync(x => x.SetProperty(m => m.Version, m => m.Version + 1));
        }
        if (scenario == "SourceVersionChanged")
        {
            await db.DataSources.Where(x => x.Id == fixture.Config.DataSourceId).ExecuteUpdateAsync(x => x.SetProperty(s => s.Version, s => s.Version + 1));
        }
        if (scenario == "PointVersionChanged")
        {
            await db.MeasurementPoints.Where(x => x.Id == fixture.Point.Id).ExecuteUpdateAsync(x => x.SetProperty(p => p.Version, p => p.Version + 1));
        }
        if (scenario == "LegacyVersions")
        {
            await db.MeasurementReadings.Where(x => x.PointId == fixture.Point.Id)
                .ExecuteUpdateAsync(x => x.SetProperty(r => r.SourceVersion, (long?)null).SetProperty(r => r.PointVersion, (long?)null));
        }
        if (scenario == "Recovered")
        {
            for (var i = 0; i < 6; i++)
            {
                clock.Now = clock.Now.AddSeconds(1);
                await scope.ServiceProvider.GetRequiredService<IMeasurementIngestor>().IngestAsync([
                    SamplePolicy.Normalize(fixture.Config, 6, "Good", clock.Now, null, clock.Now, "recover-" + i)]);
            }
        }
        var run = await Investigate(session, alarm.Id);
        Assert.Equal(trust, run.Assessment!.DataTrust);
        Assert.Equal(anomaly, run.Assessment.AnomalyEvidence);
        Assert.Contains(run.Evidence, x => x.Kind == "Alarm" && x.Facts.Any(f => f.Contains("历史告警快照", StringComparison.Ordinal)));
        var status = Assert.Single(run.Assessment.Points);
        Assert.Equal(clock.Now, status.AsOf);
        Assert.NotEmpty(status.Windows);
        if (scenario == "Fresh")
        {
            Assert.Equal(fixture.Config.SourceVersion, status.Observation!.SourceVersion);
            Assert.Equal(fixture.Config.PointVersion, status.Observation.PointVersion);
        }
        if (scenario == "LegacyVersions")
        {
            Assert.Null(status.Observation!.SourceVersion);
            Assert.Contains("ConfigurationVersionUnverified", status.Reasons);
        }
        Assert.True(status.Windows.All(x => x.To <= status.AsOf));
    }
}
