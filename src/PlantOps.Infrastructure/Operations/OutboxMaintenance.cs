using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using PlantOps.Infrastructure.Persistence;

namespace PlantOps.Infrastructure.Operations;

public sealed class OutboxMaintenance(PlantOpsDbContext db, TimeProvider clock, IConfiguration configuration)
{
    public async Task<int> RetainAsync(CancellationToken cancellationToken = default)
    {
        // Existing demonstrations and historical evidence are preserved unless explicitly opted in.
        if (!configuration.GetValue("Outbox:RetentionEnabled", false))
        {
            return 0;
        }

        var hours = configuration.GetValue("Outbox:SentRetentionHours", 24);
        var batch = configuration.GetValue("Outbox:CleanupBatchSize", 1000);
        if (hours is < 1 or > 8760 || batch is < 1 or > 5000)
        {
            throw new InvalidOperationException("Invalid Outbox retention configuration.");
        }

        var cutoff = clock.GetUtcNow().AddHours(-hours);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var rows = await db.OutboxEvents.FromSqlInterpolated($"SELECT * FROM \"OutboxEvents\" WHERE \"DispatchedAt\" < {cutoff} ORDER BY \"DispatchedAt\", \"Id\" LIMIT {batch} FOR UPDATE SKIP LOCKED").ToListAsync(cancellationToken);
        db.OutboxEvents.RemoveRange(rows);
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return rows.Count;
    }
}
