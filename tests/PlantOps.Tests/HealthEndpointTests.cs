using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PlantOps.Application.Operations;
using Xunit;

namespace PlantOps.Tests;

public sealed class HealthEndpointTests
{
    [Theory]
    [InlineData(true, HttpStatusCode.OK)]
    [InlineData(false, HttpStatusCode.ServiceUnavailable)]
    public async Task ReadinessReflectsDatabaseAvailability(bool ready, HttpStatusCode expected)
    {
        await using var factory = CreateFactory(ready);
        var response = await factory.CreateClient().GetAsync("/health/ready");
        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task LivenessDoesNotDependOnDatabase()
    {
        await using var factory = CreateFactory(false);
        var response = await factory.CreateClient().GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task DevelopmentPublishesOpenApi()
    {
        await using var factory = CreateFactory(true);
        var response = await factory.CreateClient().GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("/api/v1/system", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/openapi/v1.json")]
    [InlineData("/api/v1/system")]
    public async Task DevelopmentEndpointsAreNotExposedInProduction(string path)
    {
        await using var factory = CreateFactory(true, "Production");
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateClient().GetAsync(path)).StatusCode);
    }

    private static WebApplicationFactory<Program> CreateFactory(bool ready, string environment = "Development") =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IDatabaseReadiness>();
                services.AddScoped<IDatabaseReadiness>(_ => new StubReadiness(ready));
            });
        });

    private sealed class StubReadiness(bool ready) : IDatabaseReadiness
    {
        public Task<bool> IsReadyAsync(CancellationToken cancellationToken) => Task.FromResult(ready);
    }
}
