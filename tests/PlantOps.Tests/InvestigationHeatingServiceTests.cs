using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PlantOps.Application.AI;
using PlantOps.Application.Alarms;
using PlantOps.Application.Telemetry;
using PlantOps.Infrastructure.Persistence;
using Xunit;

namespace PlantOps.Tests;

[Collection("Database")]
public sealed class InvestigationHeatingServiceTests
{
    [DatabaseScenarioTheory]
    [InlineData("zh-CN")]
    [InlineData("en")]
    public async Task SameReadingsRaiseTemperatureAlarmAndGroundFrozenAnalysisAndDraft(string language)
    {
        var clock = new AlarmTests.Clock();
        await using var session = new AssetManagementTests.Session(clock, await TestDatabase.IsolatedConnection());
        await session.Initialize();
        await session.Login();
        var asset = await session.Create("assets", Guid.Parse("10000000-0000-0000-0000-000000000001"));
        var pointResponse = await session.Client.PostAsJsonAsync($"/api/v1/assets/{asset.Id}/measurement-points",
            new PointInput("TEMP", "Temperature", "Temperature", "degC"));
        Assert.Equal(HttpStatusCode.Created, pointResponse.StatusCode);
        var point = (await pointResponse.Content.ReadFromJsonAsync<PointOverview>())!;
        var settings = session.Factory.Services.GetRequiredService<IConfiguration>();
        settings["OpcUa:LocalRuntimeEnabled"] = "true";
        const string prefix = "OpcUa:ApprovedEndpoints:local-runtime:";
        settings[prefix + "Url"] = OpcUaRuntimeTests.Endpoint.Url;
        settings[prefix + "ApplicationUri"] = OpcUaRuntimeTests.Endpoint.ApplicationUri;
        settings[prefix + "NamespaceUri"] = OpcUaRuntimeTests.Endpoint.NamespaceUri;
        settings[prefix + "CertificateRoot"] = OpcUaRuntimeTests.Endpoint.CertificateRoot;
        settings[prefix + "Nodes:0"] = "String:plantops-p101/temperature";
        settings[prefix + "Nodes:1"] = "String:plantops-p101/reference-temperature";
        settings[prefix + "Nodes:2"] = "String:plantops-p101/vibration";
        var sourceResponse = await session.Client.PostAsJsonAsync($"/api/v1/plants/{asset.PlantId}/data-sources",
            new SourceInput("Heating fixture", Kind: "OpcUa", OpcUa: new("local-runtime")));
        var source = (await sourceResponse.Content.ReadFromJsonAsync<SourceDto>())!;
        Assert.Equal(HttpStatusCode.Created, (await session.Client.PostAsJsonAsync($"/api/v1/assets/{asset.Id}/measurement-points/{point.Id}/source-mappings",
            new MappingInput(source.Id, "temperature", RawUnit: "degC", OpcUa: new(OpcUaRuntimeTests.Endpoint.NamespaceUri, "String", "plantops-p101/temperature")))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await session.Client.PostAsJsonAsync($"/api/v1/assets/{asset.Id}/measurement-points/{point.Id}/alarm-rules",
            new RuleInput("Demo high temperature", RaiseAbove: 80, ClearBelow: 75))).StatusCode);
        foreach (var related in new[]
        {
            (Code: "REFERENCE_TEMP", Quantity: "Temperature", Unit: "degC", Node: "reference-temperature"),
            (Code: "VIB", Quantity: "Vibration", Unit: "mm/s", Node: "vibration")
        })
        {
            var relatedResponse = await session.Client.PostAsJsonAsync($"/api/v1/assets/{asset.Id}/measurement-points",
                new PointInput(related.Code, related.Code, related.Quantity, related.Unit));
            Assert.Equal(HttpStatusCode.Created, relatedResponse.StatusCode);
            var relatedPoint = (await relatedResponse.Content.ReadFromJsonAsync<PointOverview>())!;
            Assert.Equal(HttpStatusCode.Created, (await session.Client.PostAsJsonAsync($"/api/v1/assets/{asset.Id}/measurement-points/{relatedPoint.Id}/source-mappings",
                new MappingInput(source.Id, related.Node, RawUnit: related.Unit,
                    OpcUa: new(OpcUaRuntimeTests.Endpoint.NamespaceUri, "String", "plantops-p101/" + related.Node)))).StatusCode);
        }
        await using var scope = session.Factory.Services.CreateAsyncScope();
        var configs = (await scope.ServiceProvider.GetRequiredService<ISourceConfigurationReader>().GetEnabledAsync()).Where(x => x.AssetId == asset.Id).ToArray();
        var config = configs.Single(x => x.PointId == point.Id);
        var start = clock.Now;
        var runtime = scope.ServiceProvider.GetRequiredService<ISourceRuntimeStore>();
        await runtime.ActivateAsync(configs, configs.Select(x => new SamplingRevision(x.PointId, x.MappingId, x.MappingVersion, start, "Periodic", 1000, 1000, 10)).ToArray(), default);
        var ingestor = scope.ServiceProvider.GetRequiredService<IMeasurementIngestor>();
        double[] values = [60, 60, 75, 75, 90, 90, 90, 90];
        for (var i = 0; i < values.Length; i++)
        {
            clock.Now = start.AddSeconds(i);
            await ingestor.IngestAsync(configs.Select(x => SamplePolicy.Normalize(x,
                x.PointId == point.Id ? values[i] : x.CanonicalUnit == "degC" ? 62 : 4,
                "Good", clock.Now, null, clock.Now, "heat-" + x.PointId + "-" + i)).ToArray());
        }
        clock.Now = clock.Now.AddSeconds(1);
        await runtime.ReportAsync(new(source.Id, config.SourceVersion, "Connected", clock.Now, clock.Now, clock.Now, 0, null), default);
        var alarms = (await session.Client.GetFromJsonAsync<AlarmPage>($"/api/v1/alarms?plantId={asset.PlantId}&assetId={asset.Id}"))!;
        var alarm = Assert.Single(alarms.Items);
        Assert.Equal(90, alarm.TriggerValue);
        var created = await session.Client.PostAsync($"/api/v1/alarms/{alarm.Id}/investigations?language={language}", null);
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        var run = (await created.Content.ReadFromJsonAsync<InvestigationRunDto>())!;
        Assert.True(await scope.ServiceProvider.GetRequiredService<IInvestigationService>().ProcessNextAsync());
        run = (await session.Client.GetFromJsonAsync<InvestigationRunDto>($"/api/v1/investigations/{run.Id}"))!;
        Assert.True(run.Status == "Succeeded", run.Status + ":" + run.FailureCode + ":" + run.RunRecord?.GetRawText());
        Assert.Equal(3, run.Assessment!.Points.Length);
        Assert.Equal(5, run.ToolCalls);
        Assert.InRange(run.InputTokens, 1, 16000);
        var evidence = Assert.Single(run.Evidence, x => x.Kind == "EventDerivedTrend" && x.ResourceId == point.Id);
        var trend = evidence.DerivedTrend!;
        Assert.Equal(30, trend.Change!.Change);
        Assert.Equal(2, Assert.Single(trend.RiseSegments).IncreaseCount);
        Assert.Equal(alarm.Id, trend.AlarmId);
        Assert.Contains(trend.Samples, x => x.Id == alarm.TriggerReadingId);
        var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
        var ids = trend.Samples.Select(x => x.Id).ToArray();
        Assert.Equal(ids.Length, await db.MeasurementReadings.CountAsync(x => x.PointId == point.Id && ids.Contains(x.Id)));
        var expectedHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new
            {
                facts = evidence.Facts,
                pointStatus = evidence.PointStatus,
                derivedTrend = trend,
                state = evidence.State
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)))));
        Assert.Equal(expectedHash, evidence.Sha256);
        Assert.Equal("3", run.Result!.Value.GetProperty("schemaVersion").GetString());
        Assert.NotEmpty(run.Result.Value.GetProperty("findings").EnumerateArray());
        var proposalResponse = await session.Client.PostAsync($"/api/v1/investigations/{run.Id}/proposals", null);
        Assert.Equal(HttpStatusCode.OK, proposalResponse.StatusCode);
        var proposal = (await proposalResponse.Content.ReadFromJsonAsync<ActionProposalDto>())!;
        Assert.Contains(language == "en" ? "Findings (limited to the listed subjects and time scopes)" : "调查发现（仅限所列对象与时间范围）", proposal.Draft!.Description);
        if (language == "zh-CN")
        {
            Assert.Contains(run.Result.Value.GetProperty("recommendations")[0].GetProperty("task").GetString()!, proposal.Draft!.Description);
        }
        Assert.Equal(0, await db.WorkOrders.CountAsync(x => x.AssetId == asset.Id));
        clock.Now = clock.Now.AddHours(1);
        var expanded = (await session.Client.GetFromJsonAsync<InvestigationEvidence>($"/api/v1/investigations/{run.Id}/evidence/{evidence.Id}"))!;
        Assert.Equal(evidence.Sha256, expanded.Sha256);
        Assert.Equal(trend.AsOf, expanded.DerivedTrend!.AsOf);
    }
}
