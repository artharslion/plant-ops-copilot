using System.Threading.Channels;

namespace PlantOps.Application.Telemetry;

public sealed class MeasurementSourceRunner
{
    private readonly IMeasurementSource source;
    private readonly IReadOnlyList<SourceConfiguration> configurations;
    private readonly Func<IReadOnlyList<NormalizedSample>, CancellationToken, Task> commit;
    private readonly Func<IReadOnlyList<IngestionLoss>, CancellationToken, Task> recordLosses;
    private readonly Action<Exception> reportFailure;
    private readonly TimeProvider clock;
    private readonly Channel<NormalizedSample> channel;
    private readonly IngestionLossBuffer losses = new();
    private readonly TimeSpan flushInterval;
    private readonly TimeSpan shutdownTimeout;
    private readonly int batchSize;

    public MeasurementSourceRunner(IMeasurementSource source, IReadOnlyList<SourceConfiguration> configurations,
        Func<IReadOnlyList<NormalizedSample>, CancellationToken, Task> commit,
        Func<IReadOnlyList<IngestionLoss>, CancellationToken, Task> recordLosses, Action<Exception> reportFailure,
        TimeProvider clock, int capacity = 10000, int batchSize = 500, TimeSpan? flushInterval = null, TimeSpan? shutdownTimeout = null)
    {
        if (capacity is < 1 or > 10000 || batchSize is < 1 or > 500 || configurations.Count is < 1 or > 200
            || configurations.Any(x => x.SourceKind != source.Kind) || configurations.Select(x => x.DataSourceId).Distinct().Count() != 1)
        {
            throw new ArgumentException("A runner requires a bounded configuration set for one source.");
        }

        this.source = source;
        this.configurations = configurations.ToArray();
        this.commit = commit;
        this.recordLosses = recordLosses;
        this.reportFailure = reportFailure;
        this.clock = clock;
        this.batchSize = batchSize;
        this.flushInterval = flushInterval ?? TimeSpan.FromMilliseconds(500);
        this.shutdownTimeout = shutdownTimeout ?? TimeSpan.FromSeconds(5);
        if (this.flushInterval <= TimeSpan.Zero || this.shutdownTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentException("Invalid pipeline timeout.");
        }

        channel = Channel.CreateBounded<NormalizedSample>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest
        },
            sample => Record(sample, "QueueOverflow", 1, false));
    }

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        using var producerCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        using var consumerCancellation = new CancellationTokenSource();
        using var registration = stoppingToken.Register(() => consumerCancellation.CancelAfter(shutdownTimeout));
        var producer = Task.Run(() => ProduceAsync(producerCancellation.Token), CancellationToken.None);
        var consumer = ConsumeAsync(consumerCancellation.Token);
        try
        {
            await Task.WhenAll(producer, consumer);
        }
        finally
        {
            producerCancellation.Cancel();
            while (channel.Reader.TryRead(out var remaining))
            {
                Record(remaining, "ShutdownTimeout", null, true);
            }

            using var finalAttempt = new CancellationTokenSource(shutdownTimeout);
            await FlushLossesAsync(finalAttempt.Token);
        }
    }

    private async Task ProduceAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), clock);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await foreach (var sample in source.ReadAsync(configurations, ct).WithCancellation(ct))
                    {
                        if (!configurations.Contains(sample.Configuration))
                        {
                            throw new InvalidOperationException("Source emitted an unconfigured sample.");
                        }

                        if (sample.RawValueJson.Length > 8000 || sample.SourceEventKey.Length > 200)
                        {
                            Record(sample, "InvalidSample", 1, false);
                            continue;
                        }
                        channel.Writer.TryWrite(sample);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception e)
                {
                    reportFailure(e);
                    foreach (var c in configurations)
                    {
                        losses.Record(c, clock.GetUtcNow(), null, "CollectionFailure", possible: true);
                    }
                }
                if (!await timer.WaitForNextTickAsync(ct))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { channel.Writer.TryComplete(); }
    }

    private async Task ConsumeAsync(CancellationToken ct)
    {
        var batch = new List<NormalizedSample>(batchSize);
        try
        {
            while (true)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(flushInterval);
                var completed = false;
                try
                {
                    while (batch.Count < batchSize)
                    {
                        while (batch.Count < batchSize && channel.Reader.TryRead(out var sample))
                        {
                            batch.Add(sample);
                        }

                        if (batch.Count == batchSize)
                        {
                            break;
                        }

                        if (!await channel.Reader.WaitToReadAsync(deadline.Token))
                        {
                            completed = true;
                            break;
                        }
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                await FlushLossesAsync(ct);
                if (batch.Count > 0)
                {
                    var ordered = batch.OrderBy(x => x.Configuration.PointId)
                        .ThenBy(x => x.SourceTimestamp ?? x.ServerTimestamp ?? x.ReceivedAt).ThenBy(x => x.ReceivedAt).ToArray();
                    try
                    {
                        await commit(ordered, ct);
                        losses.Recover(ordered.Select(x => x.Configuration).Distinct().ToArray(), ordered.Max(x => x.ReceivedAt));
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception e)
                    {
                        reportFailure(e);
                        foreach (var sample in batch)
                        {
                            Record(sample,
                            e is ArgumentException or InvalidOperationException ? "BatchRejected" : "DatabaseUnavailable", null, true);
                        }
                    }
                    batch.Clear();
                }
                await FlushLossesAsync(ct);
                if (completed)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            foreach (var sample in batch)
            {
                Record(sample, "ShutdownTimeout", null, true);
            }
        }
    }

    private void Record(NormalizedSample sample, string reason, long? count, bool possible)
    {
        var at = sample.SourceTimestamp ?? sample.ServerTimestamp ?? sample.ReceivedAt;
        if (at > sample.ReceivedAt.AddDays(1) || at < sample.ReceivedAt.AddDays(-365))
        {
            at = sample.ReceivedAt;
        }

        losses.Record(sample.Configuration, at, at.AddTicks(1), reason, count, possible);
    }

    private async Task FlushLossesAsync(CancellationToken ct)
    {
        var snapshot = losses.Snapshot();
        if (snapshot.Length == 0)
        {
            return;
        }

        try
        {
            await recordLosses(snapshot, ct);
            losses.Acknowledge(snapshot);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception e) { reportFailure(e); }
    }
}
