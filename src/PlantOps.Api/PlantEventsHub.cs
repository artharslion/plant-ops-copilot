using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using PlantOps.Application.Operations;
using PlantOps.Infrastructure.Identity;
using PlantOps.Infrastructure.Persistence;
namespace PlantOps.Api;
public sealed record PlantConnection(string Id, string UserId, string? Stamp, Guid[] Plants, string[] Roles, Action Abort);
public sealed class PlantConnections
{
    private readonly ConcurrentDictionary<string, PlantConnection> connections = new();
    public void Add(PlantConnection c) => connections[c.Id] = c;
    public void Remove(string id) => connections.TryRemove(id, out _);
    public PlantConnection[] Snapshot() => connections.Values.ToArray();
}
[Authorize]
public sealed class PlantEventsHub(PlantOpsDbContext db, UserManager<PlantOpsUser> users, PlantConnections connections) : Hub
{
    public override async Task OnConnectedAsync()
    {
        var user = await users.GetUserAsync(Context.User!);
        if (user is not { IsEnabled: true }) { Context.Abort(); return; }
        var plants = await db.UserPlantAccess.AsNoTracking().Where(x => x.UserId == user.Id).Select(x => x.PlantId).ToArrayAsync();
        connections.Add(new(Context.ConnectionId, user.Id, user.SecurityStamp, plants, (await users.GetRolesAsync(user)).ToArray(), Context.Abort));
        await base.OnConnectedAsync();
    }
    public override Task OnDisconnectedAsync(Exception? exception) { connections.Remove(Context.ConnectionId); return base.OnDisconnectedAsync(exception); }
    // No client supplied groups, subscriptions, or business write methods.
}
public sealed class SignalRPlantPublisher(IHubContext<PlantEventsHub> hub, PlantConnections connections, PlantOpsDbContext db, UserManager<PlantOpsUser> users) : IPlantEventPublisher
{
    private bool validated;
    public async Task ValidateAsync(CancellationToken ct)
    {
        foreach (var c in connections.Snapshot())
        {
            var u = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == c.UserId, ct);
            var grants = await db.UserPlantAccess.AsNoTracking().Where(x => x.UserId == c.UserId).Select(x => x.PlantId).ToArrayAsync(ct);
            var roles = u == null ? [] : (await users.GetRolesAsync(u)).ToArray();
            if (u is not { IsEnabled: true } || u.SecurityStamp != c.Stamp || !grants.Order().SequenceEqual(c.Plants.Order()) || !roles.Order().SequenceEqual(c.Roles.Order()))
            { connections.Remove(c.Id); c.Abort(); }
        }
        validated = true;
    }
    public async Task PublishAsync(PlantEvent change, CancellationToken cancellationToken)
    {
        if (!validated) await ValidateAsync(cancellationToken);
        foreach (var c in connections.Snapshot().Where(x => x.Plants.Contains(change.PlantId)))
            await hub.Clients.Client(c.Id).SendAsync("PlantEvent", change, cancellationToken);
    }
}
public sealed class PlantEventWorker(IServiceScopeFactory scopes, ILogger<PlantEventWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken); timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await scope.ServiceProvider.GetRequiredService<SignalRPlantPublisher>().ValidateAsync(timeout.Token);
                await scope.ServiceProvider.GetRequiredService<IOutboxDispatcher>().DispatchAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception e) { logger.LogWarning("Outbox dispatch deferred ({ErrorType}); committed state remains available through HTTP.", e.GetType().Name); }
        }
    }
}
