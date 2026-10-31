using PlantOps.Application.AI;

namespace PlantOps.Worker;

public sealed class InvestigationWorker(IServiceScopeFactory scopes, ILogger<InvestigationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextPrune = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                if (DateTimeOffset.UtcNow >= nextPrune)
                {
                    await scope.ServiceProvider.GetRequiredService<IInvestigationResponseStore>().PruneAsync(stoppingToken);
                    nextPrune = DateTimeOffset.UtcNow.AddHours(1);
                }
                var processed = await scope.ServiceProvider.GetRequiredService<IInvestigationService>().ProcessNextAsync(stoppingToken);
                if (!processed)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogWarning("Investigation queue is temporarily unavailable: errorType={ErrorType}; retrying without exposing request data.", exception.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
