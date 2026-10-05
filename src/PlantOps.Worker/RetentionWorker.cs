using PlantOps.Application.Telemetry;
namespace PlantOps.Worker;
public sealed class RetentionWorker(IServiceScopeFactory scopes, ILogger<RetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DateOnly? retainedOn = null; using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope(); var maintenance = scope.ServiceProvider.GetRequiredService<IMeasurementMaintenance>();
                await maintenance.AggregateAsync(stoppingToken);
                var today = DateOnly.FromDateTime(DateTime.UtcNow);
                if (retainedOn != today)
                {
                    var result = await maintenance.RetainAsync(stoppingToken); retainedOn = today;
                    logger.LogInformation("Daily retention: {Dropped} partitions removed, {Blocked} await aggregation, {Minutes} expired minutes removed.", result.DroppedPartitions, result.BlockedPartitions, result.DeletedMinutes);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception e) { logger.LogWarning("Measurement maintenance deferred ({ErrorType}); raw data retained.", e.GetType().Name); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
