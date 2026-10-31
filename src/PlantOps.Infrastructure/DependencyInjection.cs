using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PlantOps.Application.Operations;
using PlantOps.Infrastructure.Operations;
using PlantOps.Infrastructure.Persistence;
using PlantOps.Application.Assets;
using PlantOps.Infrastructure.Assets;
using PlantOps.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using PlantOps.Application.Telemetry;
using PlantOps.Infrastructure.Telemetry;
using PlantOps.Application.Alarms;
using PlantOps.Infrastructure.Alarms;

namespace PlantOps.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddPlantOpsInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<PlantOpsDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString("PlantOps") ?? string.Empty));
        services.AddScoped<IDatabaseReadiness, DatabaseReadiness>();
        services.AddScoped<IOverviewService, OverviewService>();
        services.AddScoped<IAssetDirectory, AssetDirectory>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IMeasurementSource, SimulationSource>();
        services.AddScoped<ISourceConfigurationReader, SourceConfigurationReader>();
        services.AddSingleton<ISourceRuntimeStore, SourceRuntimeStore>();
        services.AddScoped<IMeasurementIngestor, MeasurementIngestor>();
        services.AddScoped<IIngestionGapRecorder, IngestionGapRecorder>();
        services.AddScoped<IMeasurementMaintenance, MeasurementMaintenance>();
        services.AddScoped<OutboxMaintenance>();
        services.AddScoped<ITelemetryService, TelemetryService>();
        services.AddScoped<IAlarmRuntimeReset, AlarmRuntimeReset>();
        services.AddScoped<IAlarmService, AlarmService>();
        services.AddScoped<PlantOps.Infrastructure.Maintenance.WorkOrderService>();
        services.AddScoped<PlantOps.Application.Maintenance.IWorkOrderService>(s => s.GetRequiredService<PlantOps.Infrastructure.Maintenance.WorkOrderService>());
        services.AddScoped<PlantOps.Application.Knowledge.IDocumentService, PlantOps.Infrastructure.Knowledge.DocumentService>();
        services.AddScoped<PlantOps.Application.Knowledge.IDocumentEvidenceService, PlantOps.Infrastructure.Knowledge.DocumentService>();
        services.AddScoped<PlantOps.Application.AI.IInvestigationService, PlantOps.Infrastructure.AI.InvestigationService>();
        services.AddSingleton<PlantOps.Application.AI.IInvestigationResponseStore, PlantOps.Infrastructure.AI.InvestigationResponseStore>();
        services.AddScoped<PlantOps.Application.AI.IAiSettingsService, PlantOps.Infrastructure.AI.AiSettingsService>();
        services.AddScoped<PlantOps.Application.AI.IActionProposalService, PlantOps.Infrastructure.AI.ActionProposalService>();
        services.AddSingleton<PlantOps.Application.Knowledge.IDocumentFileStore, PlantOps.Infrastructure.Knowledge.LocalDocumentFileStore>();
        services.AddIdentity<PlantOpsUser, IdentityRole>(options =>
        {
            options.Password.RequiredLength = 12;
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
        }).AddEntityFrameworkStores<PlantOpsDbContext>().AddDefaultTokenProviders();
        services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.Zero);
        services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database", tags: ["ready"],
            timeout: TimeSpan.FromSeconds(5));
        return services;
    }
}
