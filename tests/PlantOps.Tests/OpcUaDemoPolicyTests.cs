extern alias worker;

using Microsoft.Extensions.Configuration;
using PlantOps.Integrations.OpcUa;
using OpcUaDemo = worker::PlantOps.Worker.OpcUaDemo;
using Xunit;

namespace PlantOps.Tests;

public sealed class OpcUaDemoPolicyTests
{
    [Theory]
    [InlineData("Production", "plantops_tests", "new_demo", 50)]
    [InlineData("Development", "plantops", "new_demo", 50)]
    [InlineData("Development", "plantops_tests", "../other", 50)]
    [InlineData("Development", "plantops_tests", "new_demo", 600)]
    public async Task RejectsUnsafeDemoScopeBeforeAnyDatabaseOrFilesystemWrite(string environment, string database, string run, int seconds)
    {
        var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:PlantOps"] = $"Host=127.0.0.1;Database={database};Search Path=opcua_demo_{run}",
            ["OpcUaDemo:Run"] = run,
            ["OpcUaDemo:Seconds"] = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
        }).Build();
        await Assert.ThrowsAsync<InvalidOperationException>(() => OpcUaDemo.RunAsync(null!, settings, environment, CancellationToken.None));
    }

    [Theory]
    [InlineData("opc.tcp://192.168.1.10:48481")]
    [InlineData("opc.tcp://localhost.evil:48481")]
    [InlineData("opc.tcp://user@localhost:48481")]
    [InlineData("opc.tcp://localhost:48481/?q=x")]
    [InlineData("http://localhost:48481")]
    public void RejectsNonLoopbackOrAmbiguousEndpoint(string endpoint) =>
        Assert.Throws<ArgumentException>(() => OpcUaDemoSource.ValidateEndpoint(endpoint));

    [Theory]
    [InlineData("opc.tcp://localhost:48481")]
    [InlineData("opc.tcp://127.0.0.1:48481")]
    public void AcceptsOnlyExplicitLocalDemoEndpoints(string endpoint) => OpcUaDemoSource.ValidateEndpoint(endpoint);
}
