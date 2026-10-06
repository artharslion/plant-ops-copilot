using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PlantOps.Application.Telemetry;
using Xunit;

namespace PlantOps.Tests;

[Collection("Database")]
public sealed class OpcUaConfigurationTests
{
    private static readonly Guid North = Guid.Parse("10000000-0000-0000-0000-000000000001");

    [DatabaseFact]
    public async Task OnlyApprovedDisabledSourcesCanBeConfiguredAndSourceKindIsImmutable()
    {
        await using var session = new AssetManagementTests.Session();
        await session.Initialize();
        var path = $"/api/v1/plants/{North}/data-sources";
        var input = new SourceInput("OPC UA isolated configuration", false, Kind: "OpcUa", OpcUa: new("local-simulator"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await session.Client.PostAsJsonAsync(path, input)).StatusCode);
        await session.Login();
        Assert.Equal(HttpStatusCode.BadRequest, (await session.Client.PostAsJsonAsync(path, input)).StatusCode);
        session.Factory.Services.GetRequiredService<IConfiguration>()["OpcUa:ApprovedEndpoints:local-simulator:Url"] = "opc.tcp://localhost:48480/plantops";
        session.Factory.Services.GetRequiredService<IConfiguration>()["OpcUa:ApprovedEndpoints:local-simulator:NamespaceUri"] = "urn:plant-ops:simulator";
        session.Factory.Services.GetRequiredService<IConfiguration>()["OpcUa:ApprovedEndpoints:local-simulator:IdentifierPrefix"] = "North.";
        Assert.Equal(HttpStatusCode.BadRequest, (await session.Client.PostAsJsonAsync(path, input with
        {
            Enabled = true
        })).StatusCode);
        var response = await session.Client.PostAsJsonAsync(path, input);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var source = (await response.Content.ReadFromJsonAsync<SourceDto>())!;
        Assert.False(source.Enabled);
        Assert.Equal("local-simulator", source.OpcUa!.EndpointReference);
        Assert.DoesNotContain("opc.tcp", await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await session.Client.PatchAsJsonAsync(path + "/" + source.Id,
            new SourceInput(source.Name, false, source.Version, "Simulation"))).StatusCode);
        var asset = await session.Create("assets", North);
        var pointResponse = await session.Client.PostAsJsonAsync($"/api/v1/assets/{asset.Id}/measurement-points", new PointInput("OPC", "Protocol point"));
        var point = (await pointResponse.Content.ReadFromJsonAsync<PointOverview>())!;
        var mappingPath = $"/api/v1/assets/{asset.Id}/measurement-points/{point.Id}/source-mappings";
        var mapping = new MappingInput(source.Id, "configured-node", Enabled: false,
            OpcUa: new("urn:plant-ops:simulator", "String", "North.Utilities.P101.VibrationRms"));
        var mapped = await session.Client.PostAsJsonAsync(mappingPath, mapping);
        Assert.Equal(HttpStatusCode.Created, mapped.StatusCode);
        Assert.StartsWith("opcua:", (await mapped.Content.ReadFromJsonAsync<MappingDto>())!.SourceKey);
        Assert.Equal(HttpStatusCode.BadRequest, (await session.Client.PostAsJsonAsync(mappingPath, mapping with
        {
            Enabled = true
        })).StatusCode);
        await using var scope = session.Factory.Services.CreateAsyncScope();
        Assert.DoesNotContain(await scope.ServiceProvider.GetRequiredService<ISourceConfigurationReader>().GetEnabledAsync(), x => x.DataSourceId == source.Id);
    }
}
