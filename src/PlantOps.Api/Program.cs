using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using PlantOps.Infrastructure;
using PlantOps.Api;
using PlantOps.Application.Assets;
using PlantOps.Infrastructure.Identity;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddPlantOpsInfrastructure(builder.Configuration);
builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = ctx =>
    ctx.ProblemDetails.Extensions["correlationId"] = ctx.HttpContext.TraceIdentifier);
builder.Services.AddOpenApi();
builder.Services.AddAuthorization();
builder.Services.AddSignalR();
builder.Services.AddSingleton<PlantConnections>();
builder.Services.AddScoped<SignalRPlantPublisher>();
builder.Services.AddScoped<PlantOps.Application.Operations.IPlantEventPublisher>(s => s.GetRequiredService<SignalRPlantPublisher>());
builder.Services.AddScoped<PlantOps.Application.Operations.IOutboxDispatcher, PlantOps.Infrastructure.Operations.OutboxDispatcher>();
if (builder.Configuration["Realtime:Enabled"] != "false")
{
    builder.Services.AddHostedService<PlantEventWorker>();
}

builder.Services.AddHostedService<OutboxRetentionWorker>();
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o => o.MultipartBodyLengthLimit = 11 * 1024 * 1024);
builder.Services.AddAntiforgery(o =>
{
    o.HeaderName = "X-CSRF-TOKEN";
    o.Cookie.Name = "PlantOps.Antiforgery";
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
});
builder.Services.ConfigureApplicationCookie(o =>
{
    o.Cookie.Name = "PlantOps.Session";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    o.ExpireTimeSpan = TimeSpan.FromHours(8);
    o.Events.OnRedirectToLogin = ctx =>
    {
        ctx.Response.StatusCode = 401;
        return Task.CompletedTask;
    };
    o.Events.OnRedirectToAccessDenied = ctx =>
    {
        ctx.Response.StatusCode = 403;
        return Task.CompletedTask;
    };
});

var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages(async status => await Results.Problem(statusCode: status.HttpContext.Response.StatusCode)
    .ExecuteAsync(status.HttpContext));
// Health probes must remain independent of cookie validation and Identity storage.
app.UseHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.UseHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
app.UseAuthentication();
app.UseAuthorization();
app.Use(async (ctx, next) =>
{
    try
    {
        if (ctx.Request.Path.StartsWithSegments("/hubs") && ctx.Request.Headers.Origin.Count > 0
            && ctx.Request.Headers.Origin.ToString() != $"{ctx.Request.Scheme}://{ctx.Request.Host}")
        {
            await Results.Problem(statusCode: 403, title: "Same-origin realtime connection required.").ExecuteAsync(ctx);
            return;
        }
        if (ctx.User.Identity?.IsAuthenticated == true)
        {
            var users = ctx.RequestServices.GetRequiredService<UserManager<PlantOpsUser>>();
            if (await users.GetUserAsync(ctx.User) is not { IsEnabled: true })
            {
                await ctx.RequestServices.GetRequiredService<SignInManager<PlantOpsUser>>().SignOutAsync();
                await Results.Problem(statusCode: 401, title: "Session unavailable.").ExecuteAsync(ctx);
                return;
            }
        }
        if (ctx.Request.Path.StartsWithSegments("/api") && !HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method))
        {
            await ctx.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(ctx);
        }

        await next();
    }
    catch (AntiforgeryValidationException)
    {
        await Results.Problem(statusCode: 400, title: "Invalid antiforgery token. Refresh and retry.").ExecuteAsync(ctx);
    }
    catch (DirectoryException e)
    {
        await Results.Problem(statusCode: e.Status, title: e.Message).ExecuteAsync(ctx);
    }
});
app.MapAuth();
app.MapDirectory();
app.MapTelemetry();
app.MapAlarms();
app.MapWorkOrders();
app.MapDocuments();
app.MapHub<PlantEventsHub>("/hubs/plant-events");
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapGet("/api/v1/system", () => new { name = "Plant Ops Copilot", stage = "P1.7", version = "0.7.0" })
        .WithName("GetSystemInfo");
}
app.Run();

public partial class Program;
