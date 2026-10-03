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

namespace PlantOps.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddPlantOpsInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<PlantOpsDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString("PlantOps") ?? string.Empty));
        services.AddScoped<IDatabaseReadiness, DatabaseReadiness>();
        services.AddScoped<IAssetDirectory, AssetDirectory>();
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
