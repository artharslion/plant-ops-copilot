using Microsoft.EntityFrameworkCore;
using PlantOps.Application.Operations;
using PlantOps.Infrastructure.Persistence;

namespace PlantOps.Infrastructure.Operations;

internal sealed class DatabaseReadiness(PlantOpsDbContext db) : IDatabaseReadiness
{
    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        if (!await db.Database.CanConnectAsync(cancellationToken)) return false;
        return !(await db.Database.GetPendingMigrationsAsync(cancellationToken)).Any();
    }
}
