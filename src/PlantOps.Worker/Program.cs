using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using PlantOps.Infrastructure;
using PlantOps.Infrastructure.Persistence;
using PlantOps.Infrastructure.Identity;

var initializeDatabase = args.Contains("--initialize-db");
var builder = WebApplication.CreateBuilder(args.Where(arg => arg != "--initialize-db").ToArray());
builder.Services.AddPlantOpsInfrastructure(builder.Configuration);

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

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
app.Logger.LogInformation("PlantOps Worker foundation started. No measurement sources or AI jobs are enabled.");
await app.RunAsync();
