using PlantOps.Application.Telemetry;
using PlantOps.Application.Alarms;

namespace PlantOps.Worker;

public sealed class MeasurementWorker(IServiceScopeFactory scopes, IEnumerable<IMeasurementSource> sources,
    ILogger<MeasurementWorker> logger, IConfiguration configuration) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var simulationEnabled = configuration.GetValue("Telemetry:SimulationEnabled", true);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var initialized = false;
        logger.LogInformation("Single-worker simulation collection enabled. Only configured active mappings are collected.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!initialized)
                {
                    await using var startupScope = scopes.CreateAsyncScope();
                    await startupScope.ServiceProvider.GetRequiredService<IAlarmRuntimeReset>().ResetAsync(stoppingToken);
                    initialized = true;
                }
                if (!simulationEnabled) { logger.LogInformation("Pending alarm timers reset; simulation collection is disabled."); return; }
                IReadOnlyList<SourceConfiguration> configurations;
                await using (var scope = scopes.CreateAsyncScope())
                    configurations = await scope.ServiceProvider.GetRequiredService<ISourceConfigurationReader>().GetEnabledAsync(stoppingToken);
                foreach (var source in sources)
                {
                    var batch = new List<NormalizedSample>();
                    await foreach (var sample in source.ReadAsync(configurations.Where(x => x.SourceKind == source.Kind).ToList(), stoppingToken))
                    {
                        batch.Add(sample);
                        if (batch.Count == 500) { await CommitWithRetry(batch, stoppingToken); batch = []; }
                    }
                    if (batch.Count > 0) await CommitWithRetry(batch, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e) { logger.LogWarning("Collection cycle failed ({FailureType}); readings may have a gap. Next cycle will reload configuration.", e.GetType().Name); }
            try { if (!await timer.WaitForNextTickAsync(stoppingToken)) break; }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
    private async Task CommitWithRetry(List<NormalizedSample> batch, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var scope = scopes.CreateAsyncScope();
            try
            {
                await scope.ServiceProvider.GetRequiredService<IMeasurementIngestor>().IngestAsync(batch, cancellationToken);
                return;
            }
            catch (Exception e) when (attempt < 3 && e is not (OperationCanceledException or InvalidOperationException or ArgumentException))
            { await Task.Delay(TimeSpan.FromMilliseconds(attempt * 250), cancellationToken); }
        }
    }
}
