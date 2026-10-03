using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PlantOps.Application.Operations;
using PlantOps.Infrastructure.Operations;
using PlantOps.Infrastructure.Persistence;

namespace PlantOps.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddPlantOpsInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<PlantOpsDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString("PlantOps") ?? string.Empty));
        services.AddScoped<IDatabaseReadiness, DatabaseReadiness>();
        services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database", tags: ["ready"],
            timeout: TimeSpan.FromSeconds(5));
        return services;
    }
}
