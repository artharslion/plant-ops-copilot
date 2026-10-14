using PlantOps.Application.AI;

namespace PlantOps.Worker;

public sealed class InvestigationWorker(IServiceScopeFactory scopes, ILogger<InvestigationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var processed = await scope.ServiceProvider.GetRequiredService<IInvestigationService>().ProcessNextAsync(stoppingToken);
                if (!processed)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception)
            {
                logger.LogWarning("Investigation queue is temporarily unavailable; retrying without exposing request data.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
