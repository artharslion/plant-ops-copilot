using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PlantOps.Application.Maintenance;
using PlantOps.Domain.Maintenance;
using PlantOps.Infrastructure.Persistence;
using Xunit;

namespace PlantOps.Tests;

[Collection("Database")]
public sealed class WorkOrderTests
{
    private static readonly Guid Pump = Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly Guid Fan = Guid.Parse("30000000-0000-0000-0000-000000000002");

    private static async Task<WorkOrder> Create(AssetManagementTests.Session s, Guid? asset = null)
    {
        var r = await s.Client.PostAsJsonAsync("/api/v1/work-orders", new CreateWorkOrder(asset ?? Pump, "Inspect cooling pump", AssignedToUserId: s.UserId));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<WorkOrder>())!;
    }

    private static async Task<WorkOrder> Move(AssetManagementTests.Session s, WorkOrder w, string to, string? reason = null, string? result = null)
    {
        var r = await s.Client.PostAsJsonAsync($"/api/v1/work-orders/{w.Id}/transition", new TransitionWorkOrder(w.Version, to, reason, result));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<WorkOrder>())!;
    }

    [Theory]
    [InlineData("Draft", "Closed", null, null, false)]
    [InlineData("Completed", "InProgress", null, "Done", false)]
    [InlineData("InProgress", "Completed", null, " ", false)]
    [InlineData("Open", "Cancelled", "Duplicate", null, true)]
    [InlineData("Closed", "InProgress", "Rework", "Done", false)]
    public void StateRules(string from, string to, string? reason, string? result, bool allowed) => Assert.Equal(allowed, WorkOrderState.Validate(from, to, reason, result) == null);

    [DatabaseFact]
    public async Task LifecycleReworkNotesHistoryAndPersistence()
    {
        await using var s = new AssetManagementTests.Session();
        await s.Initialize("MaintenanceEngineer");
        await s.Login();
        var w = await Create(s);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PostAsJsonAsync($"/api/v1/work-orders/{w.Id}/transition", new TransitionWorkOrder(w.Version, "Closed"))).StatusCode);
        w = await Move(s, w, "Open");
        w = await Move(s, w, "InProgress");
        var old = w;
        var note = await s.Client.PostAsJsonAsync($"/api/v1/work-orders/{w.Id}/notes", new AppendWorkOrderNote(w.Version, "Checked coupling"));
        w = (await note.Content.ReadFromJsonAsync<WorkOrder>())!;
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.PostAsJsonAsync($"/api/v1/work-orders/{w.Id}/transition", new TransitionWorkOrder(old.Version, "Completed", Resolution: "Done"))).StatusCode);
        w = await Move(s, w, "Completed", result: "Aligned coupling");
        w = await Move(s, w, "InProgress", "Recheck vibration");
        Assert.Null(w.CompletedAt);
        Assert.Null(w.Resolution);
        w = await Move(s, w, "Completed", result: "Recheck passed");
        w = await Move(s, w, "Closed");
        Assert.NotNull(w.ClosedAt);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PostAsJsonAsync($"/api/v1/work-orders/{w.Id}/notes", new AppendWorkOrderNote(w.Version, "Late edit"))).StatusCode);
        var history = (await s.Client.GetFromJsonAsync<WorkOrderPage>($"/api/v1/work-orders?assetId={Pump}&history=true"))!;
        Assert.Contains(history.Items, x => x.Id == w.Id);
        var notes = (await s.Client.GetFromJsonAsync<WorkOrderNote[]>($"/api/v1/work-orders/{w.Id}/notes"))!;
        Assert.Equal(2, notes.Length);
        Assert.Contains(notes, x => x.Kind == "Rework");
        await using var scope = s.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
        Assert.Equal("Closed", (await db.WorkOrders.AsNoTracking().SingleAsync(x => x.Id == w.Id)).Status);
        Assert.Equal(8, await db.AuditLogs.CountAsync(x => x.ResourceId == w.Id));
    }

    [DatabaseFact]
    public async Task RolesScopeCsrfAssigneeAndCancellation()
    {
        foreach (var role in new[] { "Viewer", "Operator" })
        {
            await using var r = new AssetManagementTests.Session();
            await r.Initialize(role);
            await r.Login();
            Assert.Equal(HttpStatusCode.Forbidden, (await r.Client.PostAsJsonAsync("/api/v1/work-orders", new CreateWorkOrder(Pump, "Denied"))).StatusCode);
        }
        await using var s = new AssetManagementTests.Session();
        await s.Initialize();
        await s.Login();
        var w = await Create(s);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Client.PostAsJsonAsync("/api/v1/work-orders", new CreateWorkOrder(Fan, "Foreign"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Client.GetAsync($"/api/v1/work-orders?assetId={Fan}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PostAsJsonAsync("/api/v1/work-orders", new CreateWorkOrder(Pump, "Bad assignee", AssignedToUserId: "unknown"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Client.PostAsJsonAsync("/api/v1/work-orders", new CreateWorkOrder(Pump, "Bad alarm", AlarmId: Guid.NewGuid()))).StatusCode);
        s.Client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PostAsJsonAsync($"/api/v1/work-orders/{w.Id}/notes", new AppendWorkOrderNote(w.Version, "Denied"))).StatusCode);
        await s.RefreshToken();
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PostAsJsonAsync($"/api/v1/work-orders/{w.Id}/transition", new TransitionWorkOrder(w.Version, "Cancelled"))).StatusCode);
        w = await Move(s, w, "Cancelled", "Duplicate request");
        await using var foreign = new AssetManagementTests.Session();
        await foreign.Initialize(both: true);
        await foreign.Login();
        var fw = await Create(foreign, Fan);
        foreach (var suffix in new[] { "", "/notes" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await s.Client.GetAsync($"/api/v1/work-orders/{fw.Id}{suffix}")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await s.Client.PostAsJsonAsync($"/api/v1/work-orders/{fw.Id}/transition", new TransitionWorkOrder(fw.Version, "Open"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Client.PostAsJsonAsync($"/api/v1/work-orders/{fw.Id}/notes", new AppendWorkOrderNote(fw.Version, "Foreign"))).StatusCode);
    }

    [DatabaseFact]
    public async Task AlarmLinkConcurrencyAndRevocationAreChecked()
    {
        await using var s = new AssetManagementTests.Session();
        await s.Initialize();
        await s.Login();
        Guid alarmId;
        await using (var scope = s.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
            var a = new PlantOps.Domain.Alarms.Alarm
            {
                PlantId = Guid.Parse("10000000-0000-0000-0000-000000000001"),
                AssetId = Pump,
                PointId = Guid.Parse("40000000-0000-0000-0000-000000000001"),
                RuleId = Guid.Parse("70000000-0000-0000-0000-000000000001"),
                State = "Recovered",
                StartedAt = DateTimeOffset.UtcNow,
                RecoveredAt = DateTimeOffset.UtcNow,
                LastEvaluatedAt = DateTimeOffset.UtcNow
            };
            db.Alarms.Add(a);
            await db.SaveChangesAsync();
            alarmId = a.Id;
        }
        var response = await s.Client.PostAsJsonAsync("/api/v1/work-orders", new CreateWorkOrder(Pump, "Alarm followup", AlarmId: alarmId));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var w = (await response.Content.ReadFromJsonAsync<WorkOrder>())!;
        Assert.Equal(alarmId, w.AlarmId);
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(i => s.Client.PostAsJsonAsync($"/api/v1/work-orders/{w.Id}/notes", new AppendWorkOrderNote(w.Version, "Concurrent " + i))));
        Assert.Single(results, x => x.StatusCode == HttpStatusCode.OK);
        Assert.Single(results, x => x.StatusCode == HttpStatusCode.Conflict);
        w = (await s.Client.GetFromJsonAsync<WorkOrder>($"/api/v1/work-orders/{w.Id}"))!;
        await using (var scope = s.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
            var access = await db.UserPlantAccess.SingleAsync(x => x.UserId == s.UserId);
            db.UserPlantAccess.Remove(access);
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.NotFound, (await s.Client.GetAsync($"/api/v1/work-orders/{w.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Client.PostAsJsonAsync($"/api/v1/work-orders/{w.Id}/transition", new TransitionWorkOrder(w.Version, "Open"))).StatusCode);
        await using var inspect = s.Factory.Services.CreateAsyncScope();
        var persisted = inspect.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
        Assert.Equal("Recovered", (await persisted.Alarms.SingleAsync(x => x.Id == alarmId)).State);
        Assert.Single(await persisted.WorkOrderNotes.Where(x => x.WorkOrderId == w.Id).ToListAsync());
    }

    [DatabaseFact]
    public async Task SqlFailureRollsBackCreateTransitionAndNote()
    {
        await using var s = new AssetManagementTests.Session();
        await s.Initialize();
        await s.Login();
        var w = await Create(s);
        s.Failure.Enabled = true;
        Assert.Equal(HttpStatusCode.InternalServerError, (await s.Client.PostAsJsonAsync($"/api/v1/work-orders/{w.Id}/transition", new TransitionWorkOrder(w.Version, "Cancelled", "Injected"))).StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, (await s.Client.PostAsJsonAsync($"/api/v1/work-orders/{w.Id}/notes", new AppendWorkOrderNote(w.Version, "Injected"))).StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, (await s.Client.PostAsJsonAsync("/api/v1/work-orders", new CreateWorkOrder(Pump, "Injected"))).StatusCode);
        s.Failure.Enabled = false;
        var current = (await s.Client.GetFromJsonAsync<WorkOrder>($"/api/v1/work-orders/{w.Id}"))!;
        Assert.Equal(w.Version, current.Version);
        Assert.Equal("Draft", current.Status);
        Assert.Empty((await s.Client.GetFromJsonAsync<WorkOrderNote[]>($"/api/v1/work-orders/{w.Id}/notes"))!);
        await using var scope = s.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
        Assert.Equal(1, await db.AuditLogs.CountAsync(x => x.ResourceId == w.Id));
    }
}
