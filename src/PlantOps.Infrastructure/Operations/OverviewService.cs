using Microsoft.EntityFrameworkCore;
using PlantOps.Application.Assets;
using PlantOps.Application.Operations;
using PlantOps.Infrastructure.Persistence;

namespace PlantOps.Infrastructure.Operations;

public sealed class OverviewService(PlantOpsDbContext db, IAssetDirectory directory, TimeProvider clock) : IOverviewService
{
    public async Task<PlantOverview> GetAsync(Actor actor, Guid plantId, bool unacknowledgedOnly, bool mine,
        string? workOrderStatus, int limit)
    {
        var plant = await directory.GetAsync(actor, "plants", plantId);
        if (limit is < 1 or > 50 || workOrderStatus is not (null or "Draft" or "Open" or "InProgress" or "Completed"))
        {
            throw new DirectoryException(400, "Invalid overview filter or limit.");
        }

        using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
        var now = clock.GetUtcNow();
        var alarms = db.Alarms.AsNoTracking().Where(x => x.PlantId == plantId && x.State == "Active");
        var activeCount = await alarms.CountAsync();
        var unacknowledgedCount = await alarms.CountAsync(x => x.AcknowledgedAt == null);
        if (unacknowledgedOnly)
        {
            alarms = alarms.Where(x => x.AcknowledgedAt == null);
        }

        var alarmRows = await (from alarm in alarms
                               join asset in db.Assets on alarm.AssetId equals asset.Id
                               join rule in db.AlarmRules on alarm.RuleId equals rule.Id
                               orderby alarm.Severity == "Critical" ? 0 : alarm.Severity == "High" ? 1 : alarm.Severity == "Medium" ? 2 : 3,
                                   alarm.AcknowledgedAt != null, alarm.StartedAt, alarm.Id
                               select new OverviewAlarm(alarm.Id, asset.Id, asset.Code, asset.Name, rule.Name,
                                   alarm.Severity, alarm.StartedAt, alarm.AcknowledgedAt)).Take(limit).ToListAsync();

        var orders = db.WorkOrders.AsNoTracking().Where(x => x.PlantId == plantId
            && (x.Status == "Draft" || x.Status == "Open" || x.Status == "InProgress" || x.Status == "Completed"));
        var orderCount = await orders.CountAsync();
        if (mine)
        {
            orders = orders.Where(x => x.AssignedToUserId == actor.UserId);
        }

        if (workOrderStatus != null)
        {
            orders = orders.Where(x => x.Status == workOrderStatus);
        }

        var matchingOrders = await orders.CountAsync();
        var orderRows = await (from order in orders
                               join asset in db.Assets on order.AssetId equals asset.Id
                               join user in db.Users on order.AssignedToUserId equals user.Id into assignees
                               from user in assignees.DefaultIfEmpty()
                               orderby order.Priority == "Critical" ? 0 : order.Priority == "High" ? 1 : order.Priority == "Medium" ? 2 : 3,
                                   order.CreatedAt, order.Id
                               select new OverviewWorkOrder(order.Id, asset.Id, asset.Code, order.Number, order.Title,
                                   order.Priority, order.Status, user == null ? null : user.DisplayName, order.CreatedAt))
            .Take(limit).ToListAsync();

        // Only enabled points on active assets are expected to provide current observations.
        // Use latest-state trust semantics, including overflow rejection, rather than quality alone.
        var pointStates = from point in db.MeasurementPoints.AsNoTracking()
                          join asset in db.Assets on point.AssetId equals asset.Id
                          join latest in db.PointLatestStates on point.Id equals latest.PointId into states
                          from latest in states.DefaultIfEmpty()
                          where point.PlantId == plantId && point.Enabled && !asset.IsArchived && !plant.IsArchived
                          select new
                          {
                              Point = point,
                              Asset = asset,
                              Latest = latest,
                              Connected = (from mapping in db.SourceMappings
                                           join source in db.DataSources on mapping.DataSourceId equals source.Id
                                           where mapping.PointId == point.Id && mapping.Enabled && source.Enabled
                                           select mapping.Id).Any()
                          };
        var issues = pointStates.Select(x => new
        {
            x.Point.Id,
            x.Asset,
            x.Point.Name,
            LastReceivedAt = x.Latest == null ? (DateTimeOffset?)null : x.Latest.LastReceivedAt,
            Reason = !x.Connected ? "Disconnected"
                : x.Latest == null || x.Latest.LatestObservedReadingId == null || x.Latest.LatestObservedEffectiveAt == null ? "Missing"
                : (now - x.Latest.LatestObservedEffectiveAt.Value).TotalMilliseconds > x.Point.StaleAfterMs ? "Stale"
                : x.Latest.LatestObservedEffectiveAt > now.AddSeconds(5)
                    || x.Latest.LatestObservedQuality != "Good" || x.Latest.LatestObservedValue == null
                    || x.Latest.LatestObservedReadingId != x.Latest.LastGoodReadingId ? "Untrusted" : null
        }).Where(x => x.Reason != null);
        var issueCount = await issues.CountAsync();
        var issueRows = await issues.OrderBy(x => x.LastReceivedAt).ThenBy(x => x.Asset.Code).ThenBy(x => x.Id)
            .Select(x => new OverviewDataIssue(x.Id, x.Asset.Id, x.Asset.Code, x.Asset.Name, x.Name, x.Reason!, x.LastReceivedAt))
            .Take(limit).ToListAsync();

        await transaction.CommitAsync();
        return new(plantId, now, new(activeCount, unacknowledgedCount, orderCount, issueCount), alarmRows,
            unacknowledgedOnly ? unacknowledgedCount : activeCount, orderRows, matchingOrders, issueRows);
    }
}
