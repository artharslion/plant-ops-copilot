using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using PlantOps.Infrastructure;
using PlantOps.Infrastructure.Persistence;
using PlantOps.Infrastructure.Identity;
using PlantOps.Worker;
using PlantOps.AI;
using PlantOps.Application.Telemetry;
using PlantOps.Integrations.OpcUa;

var initializeDatabase = args.Contains("--initialize-db");
var opcUaDemo = args.Contains("--opcua-demo");
var investigationOnce = args.Contains("--investigation-once");
if (new[] { initializeDatabase, opcUaDemo, investigationOnce }.Count(x => x) > 1)
{
    throw new InvalidOperationException("Choose exactly one initialization or demo command.");
}
var builder = WebApplication.CreateBuilder(args.Where(arg => arg is not ("--initialize-db" or "--opcua-demo" or "--investigation-once")).ToArray());
builder.Services.AddPlantOpsInfrastructure(builder.Configuration);
builder.Services.AddPlantOpsAi(builder.Configuration);
if (!initializeDatabase && !opcUaDemo && !investigationOnce)
{
    if (builder.Configuration.GetValue("OpcUa:LocalRuntimeEnabled", false))
    {
        if (!builder.Environment.IsDevelopment())
        {
            throw new InvalidOperationException("Controlled local OPC UA runtime requires Development.");
        }
        builder.Services.AddSingleton<IOpcUaConnector>(_ => new OpcUaRuntimeConnector(reference =>
        {
            var approved = builder.Configuration.GetSection($"OpcUa:ApprovedEndpoints:{reference}");
            return new(approved["Url"] ?? "", approved["ApplicationUri"] ?? "", approved["NamespaceUri"] ?? "",
                approved.GetSection("Nodes").Get<string[]>() ?? [], approved["CertificateRoot"] ?? "");
        }, builder.Environment.IsDevelopment(), true));
        builder.Services.AddSingleton<IMeasurementSource, OpcUaRuntimeSource>();
    }
    builder.Services.AddHostedService<MeasurementWorker>();
}

if (!initializeDatabase && !opcUaDemo && !investigationOnce)
{
    builder.Services.AddHostedService<RetentionWorker>();
    builder.Services.AddHostedService<InvestigationWorker>();
}

var app = builder.Build();
if (investigationOnce)
{
    await using var scope = app.Services.CreateAsyncScope();
    var processed = await scope.ServiceProvider.GetRequiredService<PlantOps.Application.AI.IInvestigationService>()
        .ProcessNextAsync(app.Lifetime.ApplicationStopping);
    app.Logger.LogInformation("Finite investigation worker completed. Claimed a queued job: {Processed}.", processed);
    return;
}
if (opcUaDemo)
{
    await OpcUaDemo.RunAsync(app.Services, builder.Configuration, app.Environment.EnvironmentName, app.Lifetime.ApplicationStopping);
    return;
}
if (initializeDatabase)
{
    if (!app.Environment.IsDevelopment())
    {
        throw new InvalidOperationException("Development database initialization is disabled outside Development.");
    }

    await using var scope = app.Services.CreateAsyncScope();
    await DevelopmentIdentityInitializer.InitializeAsync(scope.ServiceProvider, builder.Configuration, app.Environment.EnvironmentName);
    app.Logger.LogInformation("Development database migrated and Golden Path seed initialized.");
    return;
}

app.UseHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.UseHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
// Explicit ordering also prevents minimal-host automatic authentication preceding health probes.
app.UseAuthentication();
app.Logger.LogInformation("PlantOps Worker started. Controlled local OPC UA enabled: {OpcUaEnabled}. General industrial OPC UA remains disabled.",
    builder.Configuration.GetValue("OpcUa:LocalRuntimeEnabled", false));
await app.RunAsync();
