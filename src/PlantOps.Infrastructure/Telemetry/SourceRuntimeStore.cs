using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PlantOps.Application.Telemetry;
using PlantOps.Domain.Telemetry;
using PlantOps.Infrastructure.Persistence;

namespace PlantOps.Infrastructure.Telemetry;

// Singleton facade; each SDK lifecycle operation owns an independent database scope.
public sealed class SourceRuntimeStore(IServiceScopeFactory scopes) : ISourceRuntimeStore
{
    public async Task ActivateAsync(IReadOnlyList<SourceConfiguration> configurations, IReadOnlyList<SamplingRevision> revisions, CancellationToken ct)
    {
        if (configurations.Count == 0 || revisions.Count != configurations.Count || revisions.Any(revision =>
            configurations.Count(configuration => configuration.PointId == revision.PointId && configuration.MappingId == revision.SourceMappingId
                && configuration.MappingVersion == revision.MappingVersion) != 1)
            || revisions.Select(x => x.PointId).Distinct().Count() != revisions.Count)
        {
            throw new SourceConfigurationException("SamplingIdentityRejected");
        }
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await MeasurementMaintenance.LockAsync(db, ct);
        foreach (var plant in configurations.Select(x => x.PlantId).Distinct().Order())
        {
            await LockAsync(db, plant, ct);
        }
        var current = await new SourceConfigurationReader(db).GetEnabledAsync(ct);
        if (configurations.Any(configuration => !current.Contains(configuration)))
        {
            throw new SourceConfigurationException("ConfigurationChanged");
        }
        foreach (var revision in revisions)
        {
            var history = await db.SamplingProfiles.Where(x => x.PointId == revision.PointId).ToListAsync(ct);
            var next = SamplingProfileTransitions.Activate(history, revision);
            foreach (var existing in history)
            {
                existing.EffectiveTo = next.Single(x => x.Id == existing.Id).EffectiveTo;
            }
            db.SamplingProfiles.Add(next.Last());
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task CloseAsync(IReadOnlyList<SamplingRevision> revisions, DateTimeOffset at, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await MeasurementMaintenance.LockAsync(db, ct);
        var ids = revisions.Select(x => x.PointId).ToArray();
        var plants = await db.MeasurementPoints.Where(x => ids.Contains(x.Id)).Select(x => x.PlantId).Distinct().ToListAsync(ct);
        foreach (var plant in plants.Order())
        {
            await LockAsync(db, plant, ct);
        }
        foreach (var revision in revisions)
        {
            // A late shutdown must never close a replacement session's sampling interval.
            var profile = await db.SamplingProfiles.SingleOrDefaultAsync(x => x.PointId == revision.PointId
                && x.SourceMappingId == revision.SourceMappingId && x.MappingVersion == revision.MappingVersion
                && x.EffectiveFrom == revision.EffectiveFrom && x.EffectiveTo == null, ct);
            if (profile != null)
            {
                profile.EffectiveTo = at > profile.EffectiveFrom ? at : profile.EffectiveFrom.AddTicks(10);
            }
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task ReportAsync(SourceHealth health, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var source = await db.DataSources.AsNoTracking().SingleOrDefaultAsync(x => x.Id == health.DataSourceId, ct);
        if (source == null)
        {
            return;
        }
        await LockAsync(db, source.PlantId, ct);
        if (!await db.DataSources.AnyAsync(x => x.Id == source.Id && x.Version == health.SourceVersion, ct))
        {
            return;
        }
        var row = await db.DataSourceHealth.SingleOrDefaultAsync(x => x.DataSourceId == source.Id, ct);
        if (row == null)
        {
            row = new()
            {
                DataSourceId = source.Id
            };
            db.DataSourceHealth.Add(row);
        }
        row.SourceVersion = health.SourceVersion;
        row.State = health.State;
        row.UpdatedAt = health.UpdatedAt;
        row.LastKeepAliveAt = health.LastKeepAliveAt;
        row.LastReceivedAt = health.LastReceivedAt;
        row.ReconnectCount = health.ReconnectCount;
        row.FailureCode = health.FailureCode;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task RecordLossAsync(IReadOnlyList<SourceConfiguration> configurations, DateTimeOffset at, string reason, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IIngestionGapRecorder>().RecordAsync(configurations.Select(configuration =>
            new IngestionLoss(Guid.NewGuid(), configuration, at, null, reason, null, true)).ToArray(), ct);
    }

    private static async Task LockAsync(PlantOpsDbContext db, Guid plant, CancellationToken ct) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({plant.ToString()}, 0))", ct);
}
