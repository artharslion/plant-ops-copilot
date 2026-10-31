using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace PlantOps.Tests;

public sealed class StaticWebTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "plantops-web-" + Guid.NewGuid());

    public StaticWebTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "assets"));
        File.WriteAllText(Path.Combine(root, "index.html"), "<html>PlantOps test shell</html>");
        File.WriteAllText(Path.Combine(root, "assets", "app.js"), "console.log('test');");
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/alarms")]
    [InlineData("/assets")]
    [InlineData("/assets/")]
    public async Task CombinedHostServesSpaDeepLinks(string path)
    {
        await using var factory = CreateFactory();
        var response = await factory.CreateClient().GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("PlantOps test shell", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/api/missing")]
    [InlineData("/hubs/missing")]
    [InlineData("/health/missing")]
    [InlineData("/openapi/missing")]
    [InlineData("/assets/missing.js")]
    public async Task MissingServiceRoutesAndFilesDoNotReturnSpa(string path)
    {
        await using var factory = CreateFactory();
        var response = await factory.CreateClient().GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("PlantOps test shell", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task CombinedHostServesJavascriptAndPreservesApiAuthorization()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        Assert.Contains("console.log", await client.GetStringAsync("/assets/app.js"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/plants")).StatusCode);
    }

    [Fact]
    public async Task SpaFallbackDoesNotHandleWriteRequests()
    {
        await using var factory = CreateFactory();
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateClient().PostAsync("/alarms", null)).StatusCode);
    }

    [Fact]
    public async Task StandaloneApiDoesNotEnableWebImplicitly()
    {
        await using var factory = CreateFactory(false);
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateClient().GetAsync("/alarms")).StatusCode);
    }

    public void Dispose()
    {
        Directory.Delete(root, true);
    }

    private WebApplicationFactory<Program> CreateFactory(bool enabled = true) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseWebRoot(root);
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Web:ServeStaticFiles"] = enabled.ToString(),
                ["Realtime:Enabled"] = "false",
                ["Outbox:RetentionEnabled"] = "false"
            }));
        });
}
