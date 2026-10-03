using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using PlantOps.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddPlantOpsInfrastructure(builder.Configuration);
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

var app = builder.Build();
app.UseExceptionHandler();
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapGet("/api/v1/system", () => new { name = "Plant Ops Copilot", stage = "P1.1", version = "0.1.0" })
        .WithName("GetSystemInfo");
}
app.Run();

public partial class Program;
