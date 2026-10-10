using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using PlantOps.Infrastructure;
using PlantOps.Infrastructure.Persistence;
using PlantOps.Infrastructure.Identity;
using PlantOps.Worker;
using PlantOps.AI;

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
app.Logger.LogInformation("PlantOps Worker started. Simulation, deterministic alarms and read-only investigation jobs are enabled; general OPC UA remains disabled.");
await app.RunAsync();
