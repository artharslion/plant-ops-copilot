using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using PlantOps.Application.Alarms;
using PlantOps.Application.Assets;
using PlantOps.Application.Telemetry;
using PlantOps.Domain.Alarms;
using PlantOps.Domain.Identity;
using PlantOps.Domain.Telemetry;
using PlantOps.Infrastructure.Identity;
using PlantOps.Infrastructure.Persistence;

namespace PlantOps.Infrastructure.Alarms;

public sealed class AlarmService(PlantOpsDbContext db, IAssetDirectory directory, UserManager<PlantOpsUser> users, TimeProvider clock) : IAlarmService
{
    private async Task Role(Actor actor, bool admin)
    {
        var user = await users.FindByIdAsync(actor.UserId);
        if (user is not { IsEnabled: true }) throw new DirectoryException(401, "Session unavailable.");
        var roles = await users.GetRolesAsync(user);
        if (admin ? !roles.Contains("Admin") : !roles.Intersect(new[] { "Operator", "MaintenanceEngineer", "Admin" }).Any())
            throw new DirectoryException(403, "Role does not allow this action.");
    }
    private async Task Lock(Guid plant) => await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({plant.ToString()}, 0))");
    private async Task<Alarm> Load(Actor actor, Guid id)
    {
        var alarm = await db.Alarms.SingleOrDefaultAsync(x => x.Id == id) ?? throw new DirectoryException(404, "Alarm not found.");
        await directory.GetAsync(actor, "assets", alarm.AssetId); return alarm;
    }
    private async Task<IReadOnlyList<AlarmDto>> Map(IReadOnlyList<Alarm> rows)
    {
        if (rows.Count == 0) return [];
        var pointIds = rows.Select(x => x.PointId).Distinct().ToArray(); var ruleIds = rows.Select(x => x.RuleId).Distinct().ToArray();
        var assetIds = rows.Select(x => x.AssetId).Distinct().ToArray(); var plantIds = rows.Select(x => x.PlantId).Distinct().ToArray();
        var points = await db.MeasurementPoints.AsNoTracking().Where(x => pointIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id);
        var rules = await db.AlarmRules.AsNoTracking().Where(x => ruleIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id);
        var states = await db.RuleRuntimeStates.AsNoTracking().Where(x => ruleIds.Contains(x.RuleId)).ToDictionaryAsync(x => x.RuleId);
        var latest = await db.PointLatestStates.AsNoTracking().Where(x => pointIds.Contains(x.PointId)).ToDictionaryAsync(x => x.PointId);
        var mappings = await db.SourceMappings.AsNoTracking().Where(x => pointIds.Contains(x.PointId) && x.Enabled).ToDictionaryAsync(x => x.PointId);
        var sourceIds = mappings.Values.Select(x => x.DataSourceId).ToArray();
        var sources = await db.DataSources.AsNoTracking().Where(x => sourceIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id);
        var assets = await db.Assets.AsNoTracking().Where(x => assetIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id);
        var plants = await db.Plants.AsNoTracking().Where(x => plantIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id);
        var now = clock.GetUtcNow();
        var readingIds = rows.Select(x => x.TriggerReadingId).ToArray();
        var dates = rows.Select(x => DateOnly.FromDateTime(x.StartedAt.UtcDateTime)).Distinct().ToArray();
        var availableReadings = (await db.MeasurementReadings.AsNoTracking().Where(x => dates.Contains(x.EffectiveDate) && readingIds.Contains(x.Id)).Select(x => x.Id).ToListAsync()).ToHashSet();
        return rows.Select(a =>
        {
            var point = points[a.PointId]; var rule = rules[a.RuleId]; var asset = assets[a.AssetId];
            var state = states.GetValueOrDefault(a.RuleId); var observed = latest.GetValueOrDefault(a.PointId);
            var mapping = mappings.GetValueOrDefault(a.PointId);
            var source = mapping == null ? null : sources.GetValueOrDefault(mapping.DataSourceId);
            var trusted = rule.Enabled && point.Enabled && !asset.IsArchived && !plants[a.PlantId].IsArchived && source is { Enabled: true }
                && state is { LastInputTrusted: true } && mapping != null && state.RuleVersion == rule.Version
                && state.MappingId == mapping.Id && state.MappingVersion == mapping.Version && state.SourceVersion == source.Version
                && state.PointVersion == point.Version && observed?.LatestObservedQuality == "Good"
                && observed.LatestObservedValue.HasValue && state.LastTrustedAt == observed.LatestObservedEffectiveAt
                && state.LastTrustedAt.HasValue && SamplePolicy.IsFresh(state.LastTrustedAt.Value, now, point.StaleAfterMs);
            return new AlarmDto(a.Id, a.PlantId, a.AssetId, asset.Code, asset.Name, a.PointId, point.Code, a.RuleId, rule.Name,
                a.RuleVersion, a.Severity, a.State, a.TriggerReadingId, a.TriggerValue, a.ThresholdSnapshot, a.StartedAt,
                a.RecoveredAt, a.AcknowledgedAt, a.AcknowledgedByUserId, a.LastEvaluatedAt, a.Version, trusted, availableReadings.Contains(a.TriggerReadingId));
        }).ToList();
    }
    public async Task<AlarmPage> ListAsync(Actor actor, Guid plantId, bool? active, Guid? assetId, int limit, string? cursor)
    {
        await directory.GetAsync(actor, "plants", plantId);
        if (assetId.HasValue && (await directory.GetAsync(actor, "assets", assetId.Value)).PlantId != plantId)
            throw new DirectoryException(404, "Asset not found.");
        if (limit is < 1 or > 200 || cursor?.Length > 128) throw new DirectoryException(400, "Invalid page size or cursor.");
        var query = db.Alarms.AsNoTracking().Where(x => x.PlantId == plantId && (!assetId.HasValue || x.AssetId == assetId)
            && (!active.HasValue || x.State == (active.Value ? "Active" : "Recovered")));
        if (cursor != null)
        {
            DateTimeOffset at; Guid id;
            try
            {
                var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split(':');
                if (parts.Length != 2) throw new FormatException();
                at = new DateTimeOffset(long.Parse(parts[0]), TimeSpan.Zero); id = Guid.ParseExact(parts[1], "N");
            }
            catch (Exception e) when (e is FormatException or ArgumentOutOfRangeException or OverflowException)
            { throw new DirectoryException(400, "Invalid cursor."); }
            query = query.Where(x => x.StartedAt < at || x.StartedAt == at && x.Id.CompareTo(id) < 0);
        }
        var rows = await query.OrderByDescending(x => x.StartedAt).ThenByDescending(x => x.Id).Take(limit + 1).ToListAsync();
        var page = rows.Take(limit).ToList();
        var next = rows.Count > limit ? Convert.ToBase64String(Encoding.UTF8.GetBytes($"{page[^1].StartedAt.UtcTicks}:{page[^1].Id:N}")) : null;
        return new(await Map(page), next);
    }
    public async Task<AlarmDto> GetAsync(Actor actor, Guid id) => (await Map([await Load(actor, id)])).Single();
    public async Task<IReadOnlyList<AlarmEventDto>> EventsAsync(Actor actor, Guid id)
    {
        await Load(actor, id);
        return await db.AlarmEvents.AsNoTracking().Where(x => x.AlarmId == id).OrderBy(x => x.OccurredAt).ThenBy(x => x.Id)
            .Select(x => new AlarmEventDto(x.Id, x.EventType, x.OccurredAt, x.ActorUserId, x.ReadingId, x.CorrelationId)).ToListAsync();
    }
    private void Audit(Actor actor, Guid plant, Guid id, string type, string action, string? before, object after)
    {
        db.AuditLogs.Add(new AuditLog { ActorUserId = actor.UserId, PlantId = plant, ResourceId = id, ResourceType = type,
            Action = action, Before = before, After = JsonSerializer.Serialize(after), OccurredAt = clock.GetUtcNow(), CorrelationId = actor.CorrelationId });
    }
    private async Task Save()
    {
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException) { throw new DirectoryException(409, "Version conflict. Refresh and check the alarm."); }
    }
    public async Task<AlarmDto> AcknowledgeAsync(Actor actor, Guid id, long version)
    {
        await Role(actor, false); var alarm = await Load(actor, id);
        await using var transaction = await db.Database.BeginTransactionAsync(); await Lock(alarm.PlantId);
        await Role(actor, false); await directory.GetAsync(actor, "assets", alarm.AssetId); await db.Entry(alarm).ReloadAsync();
        if (alarm.AcknowledgedAt.HasValue) return (await Map([alarm])).Single();
        if (alarm.Version != version) throw new DirectoryException(409, "Version conflict. Refresh and check the alarm.");
        var before = JsonSerializer.Serialize(alarm);
        alarm.AcknowledgedAt = clock.GetUtcNow(); alarm.AcknowledgedByUserId = actor.UserId; alarm.Version++;
        db.AlarmEvents.Add(new AlarmEvent { AlarmId = id, EventType = "Acknowledged", ActorUserId = actor.UserId,
            OccurredAt = alarm.AcknowledgedAt.Value, CorrelationId = actor.CorrelationId });
        Audit(actor, alarm.PlantId, id, "Alarm", "Acknowledged", before, alarm);
        await Save(); await transaction.CommitAsync(); return (await Map([alarm])).Single();
    }
    private async Task<MeasurementPoint> Point(Actor actor, Guid asset, Guid point)
    {
        await directory.GetAsync(actor, "assets", asset);
        return await db.MeasurementPoints.SingleOrDefaultAsync(x => x.Id == point && x.AssetId == asset)
            ?? throw new DirectoryException(404, "Point not found.");
    }
    public async Task<IReadOnlyList<RuleDto>> RulesAsync(Actor actor, Guid assetId, Guid pointId)
    {
        await Point(actor, assetId, pointId);
        var rows = await db.AlarmRules.AsNoTracking().Where(x => x.PointId == pointId).OrderBy(x => x.Id).ToListAsync();
        var ids = rows.Select(x => x.Id).ToArray();
        var states = await db.RuleRuntimeStates.AsNoTracking().Where(x => ids.Contains(x.RuleId)).ToDictionaryAsync(x => x.RuleId);
        var active = (await db.Alarms.AsNoTracking().Where(x => ids.Contains(x.RuleId) && x.State == "Active").Select(x => x.RuleId).ToListAsync()).ToHashSet();
        return rows.Select(r => new RuleDto(r.Id, r.PlantId, r.PointId, r.Name, r.RuleType, r.RaiseAbove, r.ClearBelow,
            r.RaiseForMs, r.ClearForMs, r.MaxSampleGapMs, r.Severity, r.Enabled, r.Version, active.Contains(r.Id),
            states.GetValueOrDefault(r.Id)?.PendingRaiseSince, states.GetValueOrDefault(r.Id)?.PendingClearSince,
            states.GetValueOrDefault(r.Id)?.LastTrustedAt)).ToList();
    }
    public async Task<RuleDto> SaveRuleAsync(Actor actor, Guid assetId, Guid pointId, Guid? id, RuleInput input)
    {
        await Role(actor, true); var point = await Point(actor, assetId, pointId);
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 200 || !double.IsFinite(input.RaiseAbove)
            || !double.IsFinite(input.ClearBelow) || input.ClearBelow >= input.RaiseAbove
            || input.RaiseForMs is < 0 or > 300000 || input.ClearForMs is < 0 or > 300000
            || input.MaxSampleGapMs is < 1 or > 300000 || input.Severity is not ("Low" or "Medium" or "High"))
            throw new DirectoryException(400, "Invalid threshold rule: finite clear < raise, duration 0–300000ms, severity Low/Medium/High.");
        await using var transaction = await db.Database.BeginTransactionAsync(); await Lock(point.PlantId);
        await Role(actor, true); await Point(actor, assetId, pointId); await db.Entry(point).ReloadAsync();
        if (!await db.Assets.AnyAsync(x => x.Id == assetId && !x.IsArchived) || !await db.Plants.AnyAsync(x => x.Id == point.PlantId && !x.IsArchived))
            throw new DirectoryException(400, "Archived resources cannot be configured.");
        if (input.MaxSampleGapMs < point.ExpectedIntervalMs) throw new DirectoryException(400, "Maximum gap must cover the expected sample interval.");
        var rule = id.HasValue ? await db.AlarmRules.SingleOrDefaultAsync(x => x.Id == id && x.PointId == pointId)
            ?? throw new DirectoryException(404, "Rule not found.") : new AlarmRule { Id = Guid.NewGuid(), PointId = pointId, PlantId = point.PlantId };
        string? before = null;
        if (id.HasValue)
        {
            await db.Entry(rule).ReloadAsync();
            if (rule.Version != input.Version) throw new DirectoryException(409, "Version conflict. Refresh rule configuration.");
            if (await db.Alarms.AnyAsync(x => x.RuleId == rule.Id && x.State == "Active"))
                throw new DirectoryException(400, "An active alarm prevents rule changes, including disabling. Acknowledgement does not recover it.");
            before = JsonSerializer.Serialize(rule); rule.Version++;
        }
        else
        {
            if (await db.AlarmRules.CountAsync(x => x.PointId == pointId) >= 20) throw new DirectoryException(400, "At most 20 rules per point.");
            db.AlarmRules.Add(rule);
        }
        rule.Name = input.Name; rule.RaiseAbove = input.RaiseAbove; rule.ClearBelow = input.ClearBelow;
        rule.RaiseForMs = input.RaiseForMs; rule.ClearForMs = input.ClearForMs; rule.MaxSampleGapMs = input.MaxSampleGapMs;
        rule.Severity = input.Severity; rule.Enabled = input.Enabled;
        var state = await db.RuleRuntimeStates.SingleOrDefaultAsync(x => x.RuleId == rule.Id);
        if (state != null) { await db.Entry(state).ReloadAsync(); ThresholdEvaluator.ResetPending(state); state.RuleVersion = rule.Version; state.Version++; }
        Audit(actor, point.PlantId, rule.Id, "AlarmRule", before == null ? "Create" : "Update", before, rule);
        await Save(); await transaction.CommitAsync(); return (await RulesAsync(actor, assetId, pointId)).Single(x => x.Id == rule.Id);
    }
}
