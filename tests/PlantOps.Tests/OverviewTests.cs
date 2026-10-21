using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PlantOps.Application.Operations;
using PlantOps.Domain.Maintenance;
using PlantOps.Infrastructure.Persistence;
using Xunit;

namespace PlantOps.Tests;

[Collection("Database")]
public sealed class OverviewTests
{
    private static readonly Guid North = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid South = Guid.Parse("10000000-0000-0000-0000-000000000002");

    [DatabaseFact]
    public async Task OverviewRequiresAuthenticationAndPlantScopeAndCountsBeyondListLimit()
    {
        var clock = new AlarmTests.Clock();
        await using var session = new AssetManagementTests.Session(clock, await TestDatabase.IsolatedConnection());
        await session.Initialize();
        Assert.Equal(HttpStatusCode.Unauthorized, (await session.Client.GetAsync($"/api/v1/plants/{North}/overview")).StatusCode);
        await session.Login();
        Assert.Equal(HttpStatusCode.NotFound, (await session.Client.GetAsync($"/api/v1/plants/{South}/overview")).StatusCode);
        var first = await AlarmTests.Configure(session);
        var firstAlarm = await AlarmTests.Raise(session, first, clock);
        var second = await AlarmTests.Configure(session);
        var secondAlarm = await AlarmTests.Raise(session, second, clock);
        await session.Client.PostAsJsonAsync($"/api/v1/alarms/{firstAlarm.Id}/acknowledge", new
        {
            firstAlarm.Version
        });
        await using (var scope = session.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
            foreach (var status in new[] { "Draft", "Open", "InProgress", "Completed", "Closed", "Cancelled" })
            {
                db.WorkOrders.Add(new WorkOrder
                {
                    PlantId = North,
                    AssetId = first.Asset.Id,
                    Number = status,
                    Title = status,
                    Status = status,
                    Resolution = status is "Completed" or "Closed" ? "Verified" : null,
                    CreatedByUserId = session.UserId,
                    AssignedToUserId = status == "Open" ? session.UserId : null,
                    CreatedAt = clock.Now
                });
            }

            await db.SaveChangesAsync();
        }

        var overview = (await session.Client.GetFromJsonAsync<PlantOverview>($"/api/v1/plants/{North}/overview?limit=1"))!;
        Assert.Equal(2, overview.Counts.ActiveAlarms);
        Assert.Equal(1, overview.Counts.UnacknowledgedAlarms);
        Assert.Equal(4, overview.Counts.OpenWorkOrders);
        Assert.Equal(secondAlarm.Id, Assert.Single(overview.Alarms).Id);
        Assert.Single(overview.WorkOrders);
        var mine = (await session.Client.GetFromJsonAsync<PlantOverview>($"/api/v1/plants/{North}/overview?mine=true&workOrderStatus=Open&unacknowledgedOnly=true"))!;
        Assert.Equal(overview.Counts, mine.Counts);
        Assert.Equal(1, mine.MatchingWorkOrders);
        Assert.Equal("Open", Assert.Single(mine.WorkOrders).Status);
        Assert.Single(mine.Alarms);
        foreach (var query in new[] { "limit=0", "limit=51", "workOrderStatus=Closed" })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await session.Client.GetAsync($"/api/v1/plants/{North}/overview?{query}")).StatusCode);
        }
    }

    [DatabaseFact]
    public async Task DataIssuesRespectFreshnessTrustEnabledAndArchivedState()
    {
        var clock = new AlarmTests.Clock();
        await using var session = new AssetManagementTests.Session(clock, await TestDatabase.IsolatedConnection());
        await session.Initialize();
        await session.Login();
        var fixture = await AlarmTests.Configure(session);
        await AlarmTests.Raise(session, fixture, clock);
        // Keep this scenario's scope small, without a limit hiding the point being verified.
        await using (var scope = session.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
            await db.MeasurementPoints.Where(x => x.Id != fixture.Point.Id).ExecuteUpdateAsync(update => update.SetProperty(x => x.Enabled, false));
        }

        async Task<PlantOverview> Read()
        {
            await using var scope = session.Factory.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IOverviewService>()
                .GetAsync(new PlantOps.Application.Assets.Actor(session.UserId, "overview-test"), North, false, false, null, 5);
        }
        Assert.Equal(0, (await Read()).Counts.DataIssues);
        clock.Now = clock.Now.AddMilliseconds(fixture.Point.StaleAfterMs);
        Assert.Empty((await Read()).DataIssues);
        clock.Now = clock.Now.AddMilliseconds(1);
        Assert.Equal("Stale", Assert.Single((await Read()).DataIssues).Reason);
        await using (var scope = session.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
            var state = await db.PointLatestStates.SingleAsync(x => x.PointId == fixture.Point.Id);
            state.LatestObservedEffectiveAt = clock.Now;
            state.LastGoodReadingId = Guid.NewGuid();
            await db.SaveChangesAsync();
        }

        Assert.Equal("Untrusted", Assert.Single((await Read()).DataIssues).Reason);
        await using (var scope = session.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
            await db.PointLatestStates.Where(x => x.PointId == fixture.Point.Id).ExecuteDeleteAsync();
        }

        Assert.Equal("Missing", Assert.Single((await Read()).DataIssues).Reason);
        await using (var scope = session.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
            await db.SourceMappings.Where(x => x.PointId == fixture.Point.Id).ExecuteUpdateAsync(update => update.SetProperty(x => x.Enabled, false));
        }

        Assert.Equal("Disconnected", Assert.Single((await Read()).DataIssues).Reason);
        await using (var scope = session.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
            await db.Assets.Where(x => x.Id == fixture.Asset.Id).ExecuteUpdateAsync(update => update.SetProperty(x => x.IsArchived, true));
        }

        Assert.Empty((await Read()).DataIssues);
        await using (var scope = session.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
            await db.Assets.Where(x => x.Id == fixture.Asset.Id).ExecuteUpdateAsync(update => update.SetProperty(x => x.IsArchived, false));
            await db.MeasurementPoints.Where(x => x.Id == fixture.Point.Id).ExecuteUpdateAsync(update => update.SetProperty(x => x.Enabled, false));
        }

        Assert.Empty((await Read()).DataIssues);
    }
}
