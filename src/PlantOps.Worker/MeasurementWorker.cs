using PlantOps.Application.Telemetry;
using PlantOps.Application.Alarms;

namespace PlantOps.Worker;

public sealed class MeasurementWorker(IServiceScopeFactory scopes, IEnumerable<IMeasurementSource> sources,
    ILogger<MeasurementWorker> logger, IConfiguration configuration, TimeProvider clock) : BackgroundService
{
    private sealed record Runtime(SourceConfiguration[] Configurations, int Capacity, CancellationTokenSource Cancellation, Task Task);
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var active = new Dictionary<Guid, Runtime>(); var initialized = false; var detectedStartup = false;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    if (!initialized)
                    {
                        await scope.ServiceProvider.GetRequiredService<IAlarmRuntimeReset>().ResetAsync(stoppingToken);
                        initialized = true;
                    }
                    var current = (await scope.ServiceProvider.GetRequiredService<ISourceConfigurationReader>().GetEnabledAsync(stoppingToken))
                        .Where(x => x.SourceKind != "Simulation" || configuration.GetValue("Telemetry:SimulationEnabled", true))
                        .GroupBy(x => x.DataSourceId).ToDictionary(g => g.Key, g => g.OrderBy(x => x.PointId).ToArray());
                    if (current.Count > 200) throw new InvalidOperationException("At most 200 active source runtimes are supported.");
                    var capacity = Math.Max(1, 10000 / Math.Max(1, current.Count));
                    if (!detectedStartup)
                    {
                        await scope.ServiceProvider.GetRequiredService<IIngestionGapRecorder>().DetectStartupAsync(current.Values.SelectMany(x => x).ToArray(), stoppingToken);
                        detectedStartup = true;
                    }
                    foreach (var id in active.Keys.ToArray())
                    {
                        var runtime = active[id];
                        if (!current.TryGetValue(id, out var next) || !next.SequenceEqual(runtime.Configurations) || runtime.Capacity > capacity || runtime.Task.IsCompleted)
                        {
                            runtime.Cancellation.Cancel(); await runtime.Task; runtime.Cancellation.Dispose(); active.Remove(id);
                        }
                    }
                    foreach (var (id, configs) in current.Where(x => !active.ContainsKey(x.Key)))
                    {
                        var source = sources.SingleOrDefault(x => x.Kind == configs[0].SourceKind);
                        if (source == null) continue;
                        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        var runner = new MeasurementSourceRunner(source, configs, CommitWithRetry, RecordLosses,
                            e => logger.LogWarning("Source pipeline deferred ({FailureType}); loss summaries are retained.", e.GetType().Name), clock, capacity: capacity);
                        active.Add(id, new(configs, capacity, cancellation, runner.RunAsync(cancellation.Token)));
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception e) { logger.LogWarning("Source configuration refresh deferred ({FailureType}).", e.GetType().Name); }
                await Task.Delay(TimeSpan.FromSeconds(1), clock, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            foreach (var runtime in active.Values) runtime.Cancellation.Cancel();
            await Task.WhenAll(active.Values.Select(x => x.Task));
            foreach (var runtime in active.Values) runtime.Cancellation.Dispose();
        }
    }
    private async Task RecordLosses(IReadOnlyList<IngestionLoss> losses, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IIngestionGapRecorder>().RecordAsync(losses, ct);
    }
    private async Task CommitWithRetry(IReadOnlyList<NormalizedSample> batch, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var scope = scopes.CreateAsyncScope();
            try
            {
                await scope.ServiceProvider.GetRequiredService<IMeasurementIngestor>().IngestAsync(batch, ct);
                return;
            }
            catch (Exception e) when (attempt < 3 && e is not (OperationCanceledException or InvalidOperationException or ArgumentException))
            { await Task.Delay(TimeSpan.FromMilliseconds(attempt * 250), clock, ct); }
            catch (Exception e) when (batch.Count > 1 && e is ArgumentException or InvalidOperationException)
            {
                // A bad/obsolete point must not discard other valid points in the same source batch.
                foreach (var sample in batch)
                {
                    try { await CommitWithRetry([sample], ct); }
                    catch (Exception rejected) when (rejected is ArgumentException or InvalidOperationException)
                    {
                        var at = sample.ReceivedAt;
                        await RecordLosses([new(Guid.NewGuid(), sample.Configuration, at, at.AddTicks(1), "BatchRejected", 1)], ct);
                    }
                }
                return;
            }
        }
    }
}
