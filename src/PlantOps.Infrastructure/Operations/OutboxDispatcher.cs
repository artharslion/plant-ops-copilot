using Microsoft.EntityFrameworkCore;
using PlantOps.Application.Operations;
using PlantOps.Infrastructure.Persistence;
namespace PlantOps.Infrastructure.Operations;
public sealed class OutboxDispatcher(PlantOpsDbContext db, IPlantEventPublisher publisher, TimeProvider clock) : IOutboxDispatcher
{
    public async Task<int> DispatchAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow(); await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var rows = await db.OutboxEvents.FromSqlInterpolated($"SELECT * FROM \"OutboxEvents\" WHERE \"DispatchedAt\" IS NULL AND \"AvailableAt\" <= {now} ORDER BY \"OccurredAt\", \"Id\" LIMIT 500 FOR UPDATE SKIP LOCKED").ToListAsync(cancellationToken);
        if (rows.Count == 0) return 0;
        try
        {
            // Measurement projections may be merged; business transitions remain separate events.
            var changes = rows.Where(x => x.EventType != "MeasurementChanged").Concat(rows.Where(x => x.EventType == "MeasurementChanged").GroupBy(x => x.EntityId).Select(g => g.MaxBy(x => x.EntityVersion)!)).OrderBy(x => x.OccurredAt);
            foreach (var e in changes) await publisher.PublishAsync(new(e.Id, e.PlantId, e.EntityId, e.EntityVersion, e.EventType, e.OccurredAt), cancellationToken);
            foreach (var e in rows) { e.Attempts++; e.DispatchedAt = clock.GetUtcNow(); }
            await db.SaveChangesAsync(cancellationToken); await tx.CommitAsync(cancellationToken); return rows.Count;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None); db.ChangeTracker.Clear(); var ids = rows.Select(x => x.Id).ToArray();
            await db.OutboxEvents.Where(x => ids.Contains(x.Id) && x.DispatchedAt == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.Attempts, x => x.Attempts + 1).SetProperty(x => x.AvailableAt, now.AddSeconds(Math.Min(60, 1 << Math.Min(6, rows.Max(x => x.Attempts))))), CancellationToken.None);
            throw;
        }
    }
}
