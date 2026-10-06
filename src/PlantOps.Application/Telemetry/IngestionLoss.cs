namespace PlantOps.Application.Telemetry;

public sealed record IngestionLoss(Guid Id, SourceConfiguration Configuration, DateTimeOffset From,
    DateTimeOffset? To, string Reason, long? DroppedCount, bool IsPossible = false);

public interface IIngestionGapRecorder
{
    Task RecordAsync(IReadOnlyList<IngestionLoss> losses, CancellationToken cancellationToken = default);

    Task RecoverAsync(IReadOnlyList<SourceConfiguration> configurations, DateTimeOffset at, CancellationToken cancellationToken = default);

    Task DetectStartupAsync(IReadOnlyList<SourceConfiguration> configurations, CancellationToken cancellationToken = default);
}

// Bounded by the fixed configuration set and reason set of a source runtime, not notification count.
// A failed database attempt reuses IDs and absolute counts; concurrent additions cannot be acknowledged away.
public sealed class IngestionLossBuffer
{
    private readonly object gate = new();
    private readonly Dictionary<(Guid, string), IngestionLoss> losses = [];
    private readonly Dictionary<(Guid, string), IngestionLoss> acknowledged = [];

    public void Record(SourceConfiguration configuration, DateTimeOffset from, DateTimeOffset? to,
        string reason, long? droppedCount = null, bool possible = false)
    {
        lock (gate)
        {
            var key = (configuration.PointId, reason);
            if (losses.TryGetValue(key, out var prior))
            {
                losses[key] = prior with
                {
                    From = from < prior.From ? from : prior.From,
                    To = to == null || prior.To == null ? null : to > prior.To ? to : prior.To,
                    DroppedCount = prior.DroppedCount.HasValue && droppedCount.HasValue ? prior.DroppedCount + droppedCount : null,
                    IsPossible = possible || prior.IsPossible
                };
            }
            else
            {
                losses[key] = new(Guid.NewGuid(), configuration, from, to, reason, droppedCount, possible);
            }
        }
    }

    public void Recover(IReadOnlyList<SourceConfiguration> configurations, DateTimeOffset at)
    {
        var points = configurations.Select(x => x.PointId).ToHashSet();
        lock (gate)
        {
            foreach (var (key, loss) in losses.ToArray())
            {
                if (points.Contains(key.Item1) && loss.To == null && at >= loss.From)
                {
                    losses[key] = loss with
                    {
                        To = at
                    };
                }
            }
        }
    }

    public IngestionLoss[] Snapshot()
    {
        lock (gate)
        {
            return losses.Where(x => acknowledged.GetValueOrDefault(x.Key) != x.Value).Select(x => x.Value).ToArray();
        }
    }

    public void Acknowledge(IEnumerable<IngestionLoss> persisted)
    {
        lock (gate)
        {
            foreach (var loss in persisted)
            {
                var key = (loss.Configuration.PointId, loss.Reason);
                if (losses.GetValueOrDefault(key) != loss)
                {
                    continue;
                }

                if (loss.To == null)
                {
                    acknowledged[key] = loss;
                }
                else
                {
                    losses.Remove(key);
                    acknowledged.Remove(key);
                }
            }
        }
    }
}
