using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PlantOps.Application.Telemetry;
using PlantOps.Infrastructure.Persistence;
using Xunit;

namespace PlantOps.Tests;

[Collection("Database")]
public sealed class OpcUaRuntimeStoreTests
{
    private static readonly Guid North = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid South = Guid.Parse("10000000-0000-0000-0000-000000000002");

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [DatabaseFact]
    public async Task ApprovedRuntimePersistsRevisionsGuardsReplacementAndAuthorizesHealth()
    {
        var clock = new Clock();
        var connection = new Npgsql.NpgsqlConnectionStringBuilder(await TestDatabase.IsolatedConnection()) { Pooling = false }.ConnectionString;
        await using var session = new AssetManagementTests.Session(clock, connection);
        await session.Initialize();
        await session.Login();
        var settings = session.Factory.Services.GetRequiredService<IConfiguration>();
        settings["OpcUa:LocalRuntimeEnabled"] = "true";
        var prefix = "OpcUa:ApprovedEndpoints:local-runtime:";
        settings[prefix + "Url"] = OpcUaRuntimeTests.Endpoint.Url;
        settings[prefix + "ApplicationUri"] = OpcUaRuntimeTests.Endpoint.ApplicationUri;
        settings[prefix + "NamespaceUri"] = OpcUaRuntimeTests.Endpoint.NamespaceUri;
        settings[prefix + "CertificateRoot"] = OpcUaRuntimeTests.Endpoint.CertificateRoot;
        settings[prefix + "Nodes:0"] = OpcUaRuntimeTests.Endpoint.Nodes[0];
        var sourcePath = $"/api/v1/plants/{North}/data-sources";
        var sourceResponse = await session.Client.PostAsJsonAsync(sourcePath, new SourceInput("Controlled local runtime", true,
            Kind: "OpcUa", OpcUa: new("local-runtime")));
        Assert.Equal(HttpStatusCode.Created, sourceResponse.StatusCode);
        var source = (await sourceResponse.Content.ReadFromJsonAsync<SourceDto>())!;
        var asset = await session.Create("assets", North);
        var pointResponse = await session.Client.PostAsJsonAsync($"/api/v1/assets/{asset.Id}/measurement-points", new PointInput("OPC", "Runtime"));
        var point = (await pointResponse.Content.ReadFromJsonAsync<PointOverview>())!;
        var mappingPath = $"/api/v1/assets/{asset.Id}/measurement-points/{point.Id}/source-mappings";
        var mappingResponse = await session.Client.PostAsJsonAsync(mappingPath, new MappingInput(source.Id, "configured-node", OpcUa:
            new(OpcUaRuntimeTests.Endpoint.NamespaceUri, "String", "plantops-p101/vibration")));
        Assert.Equal(HttpStatusCode.Created, mappingResponse.StatusCode);
        var invalidMapping = await session.Client.PostAsJsonAsync(mappingPath, new MappingInput(source.Id, "configured-node", OpcUa:
            new(OpcUaRuntimeTests.Endpoint.NamespaceUri, "String", "plantops-p103/vibration")));
        Assert.Equal(HttpStatusCode.BadRequest, invalidMapping.StatusCode);
        SourceConfiguration configuration;
        await using (var scope = session.Factory.Services.CreateAsyncScope())
        {
            configuration = (await scope.ServiceProvider.GetRequiredService<ISourceConfigurationReader>().GetEnabledAsync()).Single(x => x.PointId == point.Id);
        }
        var store = session.Factory.Services.GetRequiredService<ISourceRuntimeStore>();
        var start = clock.Now.AddSeconds(-2);
        var revision = new SamplingRevision(point.Id, configuration.MappingId, configuration.MappingVersion, start, "Periodic", 1000, 1000, 10);
        await store.ActivateAsync([configuration], [revision], default);
        var replacement = revision with
        {
            EffectiveFrom = clock.Now.AddSeconds(-1)
        };
        await store.ActivateAsync([configuration], [replacement], default);
        await store.CloseAsync([revision], clock.Now, default);
        await using (var scope = session.Factory.Services.CreateAsyncScope())
        {
            var profiles = await scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>().SamplingProfiles
                .Where(x => x.PointId == point.Id).OrderBy(x => x.EffectiveFrom).ToArrayAsync();
            Assert.Equal(replacement.EffectiveFrom.ToUnixTimeMilliseconds(), profiles[0].EffectiveTo!.Value.ToUnixTimeMilliseconds());
            Assert.Null(profiles[1].EffectiveTo);
        }
        await store.ReportAsync(new(source.Id, source.Version, "Connected", clock.Now, clock.Now, clock.Now, 1, null), default);
        var sources = (await session.Client.GetFromJsonAsync<SourceDto[]>(sourcePath))!;
        Assert.True(sources.Single(x => x.Id == source.Id).Health!.IsCurrent);
        var json = await session.Client.GetStringAsync(sourcePath);
        Assert.DoesNotContain("opc.tcp", json);
        Assert.DoesNotContain("CertificateRoot", json);
        clock.Now = clock.Now.AddSeconds(11);
        Assert.False((await session.Client.GetFromJsonAsync<SourceDto[]>(sourcePath))!.Single(x => x.Id == source.Id).Health!.IsCurrent);
        Assert.Equal(HttpStatusCode.NotFound, (await session.Client.GetAsync($"/api/v1/plants/{South}/data-sources")).StatusCode);
        var update = await session.Client.PatchAsJsonAsync(sourcePath + "/" + source.Id,
            new SourceInput(source.Name, false, source.Version, source.Kind, source.OpcUa));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        await Assert.ThrowsAsync<SourceConfigurationException>(() => store.ActivateAsync([configuration], [replacement with { EffectiveFrom = clock.Now }], default));
        await store.ReportAsync(new(source.Id, source.Version, "Connected", clock.Now, clock.Now, clock.Now, 2, null), default);
        await store.CloseAsync([replacement], clock.Now, default);
        await using (var scope = session.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
            Assert.Equal(1, (await db.DataSourceHealth.SingleAsync(x => x.DataSourceId == source.Id)).ReconnectCount);
            Assert.All(await db.SamplingProfiles.Where(x => x.PointId == point.Id).ToArrayAsync(), x => Assert.NotNull(x.EffectiveTo));
        }
    }
}
