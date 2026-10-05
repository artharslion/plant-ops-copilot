using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PlantOps.Api;
using PlantOps.Application.Operations;
using PlantOps.Domain.Operations;
using PlantOps.Infrastructure.Operations;
using PlantOps.Infrastructure.Persistence;
using Xunit;
namespace PlantOps.Tests;
[Collection("Database")]
public sealed class OutboxTests
{
    private sealed class Clock : TimeProvider
    { public DateTimeOffset Now { get; set; } = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero); public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Publisher : IPlantEventPublisher
    {
        public bool Fail { get; set; }
        public List<PlantEvent> Events { get; } = [];
        public Task PublishAsync(PlantEvent e, CancellationToken ct) { Events.Add(e); if (Fail) throw new IOException("Injected publish failure"); return Task.CompletedTask; }
    }
    private static PlantOpsDbContext Db() => new(new DbContextOptionsBuilder<PlantOpsDbContext>().UseNpgsql(TestDatabase.Connection).Options);
    private static OutboxEvent Event(Clock c, Guid entity, long version = 1, string type = "WorkOrderChanged") => new() { PlantId = Guid.NewGuid(), EntityId = entity, EntityVersion = version, EventType = type, OccurredAt = c.Now, AvailableAt = c.Now };
    [DatabaseFact]
    public async Task UncommittedEventsAreInvisibleAndPublishFailureRetriesSameId()
    {
        var c = new Clock(); var publisher = new Publisher(); await using var writer = Db(); await using var tx = await writer.Database.BeginTransactionAsync();
        var e = Event(c, Guid.NewGuid()); writer.OutboxEvents.Add(e); await writer.SaveChangesAsync();
        await using (var reader = Db()) await new OutboxDispatcher(reader, publisher, c).DispatchAsync();
        Assert.DoesNotContain(publisher.Events, x => x.EventId == e.Id); await tx.CommitAsync(); publisher.Fail = true;
        await using (var reader = Db()) await Assert.ThrowsAsync<IOException>(() => new OutboxDispatcher(reader, publisher, c).DispatchAsync());
        await writer.Entry(e).ReloadAsync(); Assert.Null(e.DispatchedAt); Assert.Equal(1, e.Attempts);
        publisher.Fail = false; c.Now = c.Now.AddMinutes(2);
        await using (var reader = Db()) await new OutboxDispatcher(reader, publisher, c).DispatchAsync();
        await writer.Entry(e).ReloadAsync(); Assert.NotNull(e.DispatchedAt); Assert.Equal(2, publisher.Events.Count(x => x.EventId == e.Id)); Assert.Equal(2, e.Attempts);
    }
    [DatabaseFact]
    public async Task MeasurementsMergeButWorkOrderTransitionsRemainAndClaimsDoNotDuplicate()
    {
        var c = new Clock(); c.Now = c.Now.AddDays(1); var p = new Publisher(); var point = Guid.NewGuid(); var order = Guid.NewGuid();
        var rows = new[] { Event(c, point, 1, "MeasurementChanged"), Event(c, point, 2, "MeasurementChanged"), Event(c, order, 1), Event(c, order, 2) };
        await using var db = Db(); db.OutboxEvents.AddRange(rows); await db.SaveChangesAsync();
        await using var a = Db(); await using var b = Db(); await Task.WhenAll(new OutboxDispatcher(a, p, c).DispatchAsync(), new OutboxDispatcher(b, p, c).DispatchAsync());
        Assert.Single(p.Events, x => x.EntityId == point); Assert.Equal(2, p.Events.Single(x => x.EntityId == point).EntityVersion);
        Assert.Equal(2, p.Events.Count(x => x.EntityId == order));
        Assert.Equal(4, await db.OutboxEvents.CountAsync(x => rows.Select(r => r.Id).Contains(x.Id) && x.DispatchedAt != null));
    }
    [DatabaseFact]
    public async Task CrashAfterSendBeforeCommitReplaysIdenticalEventId()
    {
        await using var s = new AssetManagementTests.Session(); await s.Initialize(); var c = new Clock(); c.Now = c.Now.AddDays(2); var p = new Publisher(); var e = Event(c, Guid.NewGuid());
        await using (var scope = s.Factory.Services.CreateAsyncScope()) { var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>(); db.OutboxEvents.Add(e); await db.SaveChangesAsync(); }
        s.Failure.Enabled = true;
        await using (var scope = s.Factory.Services.CreateAsyncScope()) await Assert.ThrowsAsync<InvalidOperationException>(() => new OutboxDispatcher(scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>(), p, c).DispatchAsync());
        s.Failure.Enabled = false;
        await using (var db = Db()) { var stored = await db.OutboxEvents.SingleAsync(x => x.Id == e.Id); Assert.Null(stored.DispatchedAt); Assert.Equal(1, stored.Attempts); }
        c.Now = c.Now.AddMinutes(2); await using (var db = Db()) await new OutboxDispatcher(db, p, c).DispatchAsync();
        Assert.Equal(2, p.Events.Count(x => x.EventId == e.Id));
    }
    [DatabaseFact]
    public async Task ActiveRealtimeConnectionsAreAbortedOnScopeRoleOrEnablementChange()
    {
        await using var s = new AssetManagementTests.Session(); await s.Initialize();
        await using var scope = s.Factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
        var user = await db.Users.SingleAsync(x => x.Id == s.UserId); var grants = await db.UserPlantAccess.Where(x => x.UserId == user.Id).Select(x => x.PlantId).ToArrayAsync();
        var connections = scope.ServiceProvider.GetRequiredService<PlantConnections>(); var aborted = false;
        connections.Add(new("test-connection", user.Id, user.SecurityStamp, grants, ["Admin"], () => aborted = true));
        var publisher = scope.ServiceProvider.GetRequiredService<SignalRPlantPublisher>(); await publisher.ValidateAsync(default); Assert.False(aborted);
        user.IsEnabled = false; await db.SaveChangesAsync(); await publisher.ValidateAsync(default); Assert.True(aborted); Assert.DoesNotContain(connections.Snapshot(), x => x.Id == "test-connection");
        user.IsEnabled = true; await db.SaveChangesAsync(); aborted = false;
        connections.Add(new("test-connection", user.Id, user.SecurityStamp, [], ["Admin"], () => aborted = true)); await publisher.ValidateAsync(default); Assert.True(aborted);
    }
}
