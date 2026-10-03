using Microsoft.Extensions.Diagnostics.HealthChecks;
using PlantOps.Application.Operations;

namespace PlantOps.Infrastructure.Operations;

internal sealed class DatabaseHealthCheck(IDatabaseReadiness database) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            return await database.IsReadyAsync(cancellationToken)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("Database or schema unavailable.");
        }
        catch (Exception)
        {
            return HealthCheckResult.Unhealthy("Database or schema unavailable.");
        }
    }
}
