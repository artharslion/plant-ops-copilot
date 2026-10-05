using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using PlantOps.Infrastructure;
using PlantOps.Infrastructure.Persistence;
using PlantOps.Infrastructure.Identity;
using PlantOps.Worker;

var initializeDatabase = args.Contains("--initialize-db");
var builder = WebApplication.CreateBuilder(args.Where(arg => arg != "--initialize-db").ToArray());
builder.Services.AddPlantOpsInfrastructure(builder.Configuration);
if (!initializeDatabase) builder.Services.AddHostedService<MeasurementWorker>();
if (!initializeDatabase) builder.Services.AddHostedService<RetentionWorker>();

var app = builder.Build();
if (initializeDatabase)
{
    if (!app.Environment.IsDevelopment())
        throw new InvalidOperationException("Development database initialization is disabled outside Development.");
    await using var scope = app.Services.CreateAsyncScope();
    await DevelopmentIdentityInitializer.InitializeAsync(scope.ServiceProvider, builder.Configuration, app.Environment.EnvironmentName);
    app.Logger.LogInformation("Development database migrated and Golden Path seed initialized.");
    return;
}

app.UseHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.UseHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
// Explicit ordering also prevents minimal-host automatic authentication preceding health probes.
app.UseAuthentication();
app.Logger.LogInformation("PlantOps Worker started. Simulation and deterministic alarm rules are enabled; OPC UA and AI are not configured.");
await app.RunAsync();
