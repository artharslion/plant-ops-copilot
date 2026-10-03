namespace PlantOps.Application.Operations;

public interface IDatabaseReadiness
{
    Task<bool> IsReadyAsync(CancellationToken cancellationToken);
}
