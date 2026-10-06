using PlantOps.Infrastructure.Operations;

namespace PlantOps.Api;

public sealed class OutboxRetentionWorker(IServiceScopeFactory scopes, IConfiguration configuration,
    ILogger<OutboxRetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Outbox:RetentionEnabled", false)) return;
        var interval = configuration.GetValue("Outbox:CleanupIntervalSeconds", 5);
        if (interval is < 1 or > 300) throw new InvalidOperationException("Invalid Outbox cleanup interval.");
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(interval));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var deleted = await scope.ServiceProvider.GetRequiredService<OutboxMaintenance>().RetainAsync(timeout.Token);
                logger.LogInformation("Outbox retention removed {SentEvents} expired sent notifications.", deleted);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception e) { logger.LogWarning("Outbox retention deferred ({FailureType}); notification history retained.", e.GetType().Name); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
