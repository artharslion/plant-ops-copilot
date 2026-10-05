namespace PlantOps.Application.Operations;
public sealed record PlantEvent(Guid EventId, Guid PlantId, Guid EntityId, long EntityVersion, string EventType, DateTimeOffset OccurredAt);
public interface IPlantEventPublisher { Task PublishAsync(PlantEvent change, CancellationToken cancellationToken); }
public interface IOutboxDispatcher { Task<int> DispatchAsync(CancellationToken cancellationToken = default); }
