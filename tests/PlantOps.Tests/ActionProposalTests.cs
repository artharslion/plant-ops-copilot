using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using PlantOps.Application.AI;
using PlantOps.Application.Alarms;
using PlantOps.Application.Assets;
using PlantOps.Application.Knowledge;
using PlantOps.Application.Maintenance;
using PlantOps.Domain.Maintenance;
using PlantOps.Infrastructure.Identity;
using PlantOps.Infrastructure.Persistence;
using Xunit;

namespace PlantOps.Tests;

[Collection("Database")]
public sealed class ActionProposalTests
{
    private static async Task<string> IsolatedConnection()
    {
        // Each schema has a distinct connection string; keeping one pool per scenario exhausts PostgreSQL in full-suite runs.
        var connection = new Npgsql.NpgsqlConnectionStringBuilder(await TestDatabase.IsolatedConnection()) { Pooling = false };
        return connection.ConnectionString;
    }

    private sealed class PauseApprovalCommit : SaveChangesInterceptor
    {
        public bool Enabled
        {
            get; set;
        }

        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (Enabled && (eventData.Context!.ChangeTracker.Entries<WorkOrder>().Any(x => x.Entity.AiProposalId.HasValue)
                || eventData.Context.ChangeTracker.Entries<WorkOrderNote>().Any(x => x.Entity.AiProposalId.HasValue)))
            {
                Reached.TrySetResult();
                await Resume.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
            return result;
        }
    }

    private static string Path(Guid id) => $"/api/v1/action-proposals/{id}";

    private static ConfirmActionProposal Confirm(ActionProposalDto proposal, string key = "confirm-one") =>
        new(proposal.Version, proposal.PayloadHash, key);

    private static async Task<InvestigationRunDto> Investigate(AssetManagementTests.Session session, AlarmTests.Clock clock, bool document = false, string language = "zh-CN")
    {
        var fixture = await AlarmTests.Configure(session);
        var alarm = await AlarmTests.Raise(session, fixture, clock);
        if (document)
        {
            var upload = await DocumentTests.Upload(session, new(fixture.Asset.PlantId, fixture.Asset.Id, "振动规程"),
                content: Encoding.UTF8.GetBytes("# vibration 振动\n\n维护工程师应核查机械松动，不能将假设当作确诊。"));
            await DocumentTests.Publish(session, (await upload.Content.ReadFromJsonAsync<DocumentDto>())!, 1);
        }
        var response = await session.Client.PostAsync($"/api/v1/alarms/{alarm.Id}/investigations?language={language}", null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var run = (await response.Content.ReadFromJsonAsync<InvestigationRunDto>())!;
        await using var scope = session.Factory.Services.CreateAsyncScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<IInvestigationService>().ProcessNextAsync());
        run = (await session.Client.GetFromJsonAsync<InvestigationRunDto>($"/api/v1/investigations/{run.Id}"))!;
        Assert.Equal("Succeeded", run.Status);
        return run;
    }

    private static async Task<ActionProposalDto> Propose(AssetManagementTests.Session session, Guid runId)
    {
        var response = await session.Client.PostAsync($"/api/v1/investigations/{runId}/proposals", null);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ActionProposalDto>())!;
    }

    [DatabaseFact]
    public async Task EnglishSimulatedInvestigationCreatesAnEnglishWorkOrderAndNoteWithoutChangingApprovedContent()
    {
        var clock = new AlarmTests.Clock();
        await using var session = new AssetManagementTests.Session(clock, await IsolatedConnection());
        await session.Initialize();
        await session.Login();
        var run = await Investigate(session, clock, language: "en");
        Assert.Equal("en", run.Language);
        Assert.True(run.Simulated);
        var proposal = await Propose(session, run.Id);
        Assert.Equal(run.Result!.Value.GetProperty("workOrder").GetProperty("draft").GetProperty("title").GetString(), proposal.Draft!.Title);
        Assert.DoesNotMatch(@"\p{IsCJKUnifiedIdeographs}", proposal.Draft.Description);
        var response = await session.Client.PostAsJsonAsync(Path(proposal.Id) + "/execute", Confirm(proposal));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var receipt = (await response.Content.ReadFromJsonAsync<ActionProposalDto>())!;
        await using var scope = session.Factory.Services.CreateAsyncScope();
        var order = await scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>().WorkOrders.SingleAsync(x => x.Id == receipt.ResultEntityId);
        Assert.Equal(proposal.Draft.Title, order.Title);
        Assert.Equal(proposal.Draft.Description, order.Description);
        var note = await ProposeNote(session, run, order);
        Assert.Equal(proposal.Draft.Description, note.NoteDraft!.Text);
    }

    private static async Task<WorkOrder> NoteTarget(AssetManagementTests.Session session, Guid assetId)
    {
        var response = await session.Client.PostAsJsonAsync("/api/v1/work-orders", new CreateWorkOrder(assetId, "Existing maintenance"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<WorkOrder>())!;
    }

    private static async Task<ActionProposalDto> ProposeNote(AssetManagementTests.Session session, InvestigationRunDto run, WorkOrder order)
    {
        var response = await session.Client.PostAsJsonAsync($"/api/v1/investigations/{run.Id}/note-proposals", new CreateNoteProposal(order.Id, order.Version));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ActionProposalDto>())!;
    }

    [DatabaseFact]
    public async Task NoteApprovalEditsTextAndConcurrentRetriesReturnOneNoteWithAuditOutboxAndDurableReceipt()
    {
        var clock = new AlarmTests.Clock();
        await using var session = new AssetManagementTests.Session(clock, await IsolatedConnection());
        await session.Initialize();
        await session.Login();
        var run = await Investigate(session, clock);
        var order = await NoteTarget(session, run.AssetId);
        var proposal = await ProposeNote(session, run, order);
        Assert.Equal("AppendWorkOrderNote", proposal.ActionType);
        Assert.Null(proposal.Draft);
        Assert.Equal(order.Id, proposal.TargetWorkOrderId);
        Assert.Equal(order.Version, proposal.TargetWorkOrderVersion);
        Assert.Contains("调查发现（仅限所列对象与时间范围）", proposal.NoteDraft!.Text);
        await using var scope = session.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
        Assert.Empty(await db.WorkOrderNotes.ToListAsync());
        var editedText = "人工核对\n\n" + proposal.NoteDraft.Text;
        var edit = await session.Client.PatchAsJsonAsync(Path(proposal.Id) + "/note", new EditNoteProposal(proposal.Version, new(editedText)));
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        var saved = (await edit.Content.ReadFromJsonAsync<ActionProposalDto>())!;
        Assert.NotEqual(proposal.PayloadHash, saved.PayloadHash);
        Assert.Equal(proposal.ExpiresAt, saved.ExpiresAt);
        Assert.Empty(await db.WorkOrderNotes.ToListAsync());
        Assert.Equal(HttpStatusCode.Conflict, (await session.Client.PostAsJsonAsync(Path(proposal.Id) + "/execute", Confirm(proposal))).StatusCode);
        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => session.Client.PostAsJsonAsync(Path(saved.Id) + "/execute", Confirm(saved, "note-" + i))));
        var receipts = new List<ActionProposalDto>();
        foreach (var response in responses)
        {
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            receipts.Add((await response.Content.ReadFromJsonAsync<ActionProposalDto>())!);
        }
        var note = await db.WorkOrderNotes.SingleAsync(x => x.AiProposalId == proposal.Id);
        Assert.Equal("AIInvestigation", note.Kind);
        Assert.Equal(order.Id, note.WorkOrderId);
        Assert.Equal(editedText.Trim(), note.Text);
        Assert.All(receipts, receipt => Assert.Equal(note.Id, receipt.ResultEntityId));
        Assert.Equal(order.Version + 1, await db.WorkOrders.Where(x => x.Id == order.Id).Select(x => x.Version).SingleAsync());
        Assert.Equal(1, await db.WorkOrders.CountAsync());
        Assert.Equal(1, await db.AuditLogs.CountAsync(x => x.ResourceId == proposal.Id && x.Action == "Execute"));
        Assert.Equal(1, await db.AuditLogs.CountAsync(x => x.ResourceId == order.Id && x.Action == "AppendNote"));
        Assert.Equal(2, await db.OutboxEvents.CountAsync(x => x.EntityId == order.Id && x.EventType == "WorkOrderChanged"));
        var receipt = receipts[0];
        Assert.True(await db.AuditLogs.AnyAsync(x => x.ResourceId == note.Id && x.CorrelationId == receipt.ApprovalCorrelationId));
        await using var fresh = session.Factory.Services.CreateAsyncScope();
        var restored = await fresh.ServiceProvider.GetRequiredService<IActionProposalService>().GetAsync(new(session.UserId, "new-session"), proposal.Id);
        Assert.Equal(note.Id, restored.ResultEntityId);
        Assert.Equal(order.Id, restored.TargetWorkOrderId);
    }

    [DatabaseFact]
    public async Task NoteTargetCannotBeChangedAndChangedOrTerminalOrdersInvalidatePendingApproval()
    {
        var clock = new AlarmTests.Clock();
        await using var session = new AssetManagementTests.Session(clock, await IsolatedConnection());
        await session.Initialize();
        await session.Login();
        var run = await Investigate(session, clock);
        var order = await NoteTarget(session, run.AssetId);
        var otherAsset = await session.Create("assets", order.PlantId);
        var other = await NoteTarget(session, otherAsset.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await session.Client.PostAsJsonAsync($"/api/v1/investigations/{run.Id}/note-proposals", new CreateNoteProposal(other.Id, other.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await session.Client.PostAsJsonAsync($"/api/v1/investigations/{run.Id}/note-proposals", new CreateNoteProposal(order.Id, 99))).StatusCode);
        var proposal = await ProposeNote(session, run, order);
        Assert.Equal(HttpStatusCode.BadRequest, (await session.Client.PatchAsJsonAsync(Path(proposal.Id) + "/note", new
        {
            version = proposal.Version,
            draft = new
            {
                text = "approved",
                workOrderId = other.Id
            }
        })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await session.Client.PatchAsJsonAsync(Path(proposal.Id), new EditActionProposal(proposal.Version, new("wrong action", "text")))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await session.Client.PatchAsJsonAsync(Path(proposal.Id) + "/note", new EditNoteProposal(proposal.Version, new(" ")))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await session.Client.PostAsJsonAsync($"/api/v1/work-orders/{order.Id}/notes", new AppendWorkOrderNote(order.Version, "Manual update"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await session.Client.PostAsJsonAsync(Path(proposal.Id) + "/execute", Confirm(proposal))).StatusCode);
        Assert.Equal("Invalidated", (await session.Client.GetFromJsonAsync<ActionProposalDto>(Path(proposal.Id)))!.Status);
        order = (await session.Client.GetFromJsonAsync<WorkOrder>($"/api/v1/work-orders/{order.Id}"))!;
        var terminalProposal = await ProposeNote(session, run, order);
        await using var scope = session.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
        await db.WorkOrders.Where(x => x.Id == order.Id).ExecuteUpdateAsync(update => update.SetProperty(x => x.Status, "Cancelled"));
        Assert.Equal(HttpStatusCode.Conflict, (await session.Client.PostAsJsonAsync(Path(terminalProposal.Id) + "/execute", Confirm(terminalProposal))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await session.Client.PostAsJsonAsync($"/api/v1/investigations/{run.Id}/note-proposals", new CreateNoteProposal(order.Id, order.Version))).StatusCode);
        Assert.Equal(1, await db.WorkOrderNotes.CountAsync());
        Assert.False(await db.WorkOrderNotes.AnyAsync(x => x.AiProposalId != null));
    }

    [DatabaseFact]
    public async Task NoteFailureAfterSqlAndFinalExpiryRollBackNoteVersionAuditOutboxAndReceipt()
    {
        var clock = new AlarmTests.Clock();
        var gate = new PauseApprovalCommit();
        await using var session = new AssetManagementTests.Session(clock, await IsolatedConnection(),
            services => services.AddDbContext<PlantOpsDbContext>(options => options.AddInterceptors(gate)));
        await session.Initialize();
        await session.Login();
        var run = await Investigate(session, clock);
        var order = await NoteTarget(session, run.AssetId);
        var proposal = await ProposeNote(session, run, order);
        session.Failure.Enabled = true;
        Assert.Equal(HttpStatusCode.InternalServerError, (await session.Client.PostAsJsonAsync(Path(proposal.Id) + "/execute", Confirm(proposal))).StatusCode);
        session.Failure.Enabled = false;
        gate.Enabled = true;
        var execution = session.Client.PostAsJsonAsync(Path(proposal.Id) + "/execute", Confirm(proposal));
        try
        {
            await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            clock.Now = clock.Now.AddMinutes(16);
        }
        finally
        {
            gate.Resume.TrySetResult();
        }
        Assert.Equal(HttpStatusCode.Conflict, (await execution).StatusCode);
        gate.Enabled = false;
        await using var scope = session.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
        Assert.Empty(await db.WorkOrderNotes.ToListAsync());
        Assert.Equal(order.Version, await db.WorkOrders.Where(x => x.Id == order.Id).Select(x => x.Version).SingleAsync());
        Assert.Equal(1, await db.OutboxEvents.CountAsync(x => x.EntityId == order.Id && x.EventType == "WorkOrderChanged"));
        Assert.False(await db.AuditLogs.AnyAsync(x => x.ResourceId == proposal.Id && x.Action == "Execute"));
        Assert.Equal("Expired", (await session.Client.GetFromJsonAsync<ActionProposalDto>(Path(proposal.Id)))!.Status);
        var freshProposal = await ProposeNote(session, run, order);
        Assert.Equal(HttpStatusCode.OK, (await session.Client.PostAsJsonAsync(Path(freshProposal.Id) + "/execute", Confirm(freshProposal))).StatusCode);
        Assert.Equal(1, await db.WorkOrderNotes.CountAsync());
    }

    [DatabaseFact]
    public async Task TwoDifferentNoteProposalsForTheSameOrderVersionCannotBothExecute()
    {
        var clock = new AlarmTests.Clock();
        await using var session = new AssetManagementTests.Session(clock, await IsolatedConnection());
        await session.Initialize();
        await session.Login();
        var run = await Investigate(session, clock);
        var order = await NoteTarget(session, run.AssetId);
        var first = await ProposeNote(session, run, order);
        var second = await ProposeNote(session, run, order);
        var responses = await Task.WhenAll(new[] { first, second }.Select(proposal => session.Client.PostAsJsonAsync(Path(proposal.Id) + "/execute", Confirm(proposal))));
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Conflict);
        await using var scope = session.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
        Assert.Equal(1, await db.WorkOrderNotes.CountAsync());
        Assert.Equal(order.Version + 1, await db.WorkOrders.Where(x => x.Id == order.Id).Select(x => x.Version).SingleAsync());
        Assert.Equal(1, await db.ActionProposals.CountAsync(x => x.Status == "Executed"));
        Assert.Equal(1, await db.ActionProposals.CountAsync(x => x.Status == "Invalidated"));
    }

    [DatabaseFact]
    public async Task NoteApprovalRevalidatesEvidenceAndPermissionsAndHidesRevokedDraft()
    {
        var clock = new AlarmTests.Clock();
        await using var session = new AssetManagementTests.Session(clock, await IsolatedConnection());
        await session.Initialize();
        await session.Login();
        var run = await Investigate(session, clock, true);
        var order = await NoteTarget(session, run.AssetId);
        var proposal = await ProposeNote(session, run, order);
        await using var scope = session.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
        var role = await db.Roles.SingleAsync(x => x.Name == "Admin");
        await db.UserRoles.Where(x => x.UserId == session.UserId).ExecuteDeleteAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await session.Client.PostAsJsonAsync(Path(proposal.Id) + "/execute", Confirm(proposal))).StatusCode);
        db.UserRoles.Add(new IdentityUserRole<string> { UserId = session.UserId, RoleId = role.Id });
        await db.SaveChangesAsync();
        var document = run.Evidence.First(x => x.Kind == "Document");
        await db.Documents.Where(x => x.Id == document.ResourceId).ExecuteUpdateAsync(update => update.SetProperty(x => x.CurrentPublishedVersionId, (Guid?)null));
        Assert.Equal(HttpStatusCode.Conflict, (await session.Client.PostAsJsonAsync(Path(proposal.Id) + "/execute", Confirm(proposal))).StatusCode);
        var hidden = (await session.Client.GetFromJsonAsync<ActionProposalDto>(Path(proposal.Id)))!;
        Assert.Equal("Invalidated", hidden.Status);
        Assert.Null(hidden.NoteDraft);
        Assert.Null(hidden.Investigation);
        Assert.Empty(await db.WorkOrderNotes.ToListAsync());
    }

    [DatabaseFact]
    public async Task SuccessfulInvestigationCreatesOnlyAProposalUntilExplicitApprovalAndReceiptSurvivesNewSession()
    {
        var clock = new AlarmTests.Clock();
        var connection = await IsolatedConnection();
        await using var session = new AssetManagementTests.Session(clock, connection);
        await session.Initialize();
        await session.Login();
        var run = await Investigate(session, clock, true);
        var proposal = await Propose(session, run.Id);
        Assert.Equal("Pending", proposal.Status);
        Assert.Contains("调查发现（仅限所列对象与时间范围）", proposal.Draft!.Description);
        Assert.Contains("Simulation", proposal.Draft.Description);
        Assert.Contains("按序检查与记录", proposal.Draft.Description);
        var recommendation = run.Result!.Value.GetProperty("recommendations")[0];
        Assert.Contains(recommendation.GetProperty("completionCriteria").GetString()!, proposal.Draft.Description);
        Assert.Contains(recommendation.GetProperty("escalation").GetProperty("condition").GetString()!, proposal.Draft.Description);
        Assert.Contains(recommendation.GetProperty("record").GetString()!, proposal.Draft.Description);
        Assert.Equal(1, proposal.Draft.Description.Split("观测来源：", StringSplitOptions.None).Length - 1);
        Assert.Equal(run.AlarmId, proposal.AlarmId);
        Assert.Equal(run.AssetId, proposal.AssetId);
        await using var scope = session.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
        Assert.Empty(await db.WorkOrders.Where(x => x.AiProposalId == proposal.Id).ToListAsync());
        var edited = proposal.Draft with
        {
            Title = "人工核对后的检查任务",
            Priority = "High",
            AssignedToUserId = session.UserId
        };
        var saved = await session.Client.PatchAsJsonAsync(Path(proposal.Id), new EditActionProposal(proposal.Version, edited));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var current = (await saved.Content.ReadFromJsonAsync<ActionProposalDto>())!;
        Assert.Equal(proposal.ExpiresAt, current.ExpiresAt);
        Assert.NotEqual(proposal.PayloadHash, current.PayloadHash);
        Assert.Empty(await db.WorkOrders.Where(x => x.AiProposalId == proposal.Id).ToListAsync());
        var execute = await session.Client.PostAsJsonAsync(Path(proposal.Id) + "/execute", Confirm(current));
        Assert.True(execute.IsSuccessStatusCode, await execute.Content.ReadAsStringAsync());
        var receipt = (await execute.Content.ReadFromJsonAsync<ActionProposalDto>())!;
        Assert.Equal("Executed", receipt.Status);
        var workOrder = await db.WorkOrders.AsNoTracking().SingleAsync(x => x.AiProposalId == proposal.Id);
        Assert.Equal(receipt.ResultEntityId, workOrder.Id);
        Assert.Equal("Draft", workOrder.Status);
        Assert.Equal("AIProposal", workOrder.Source);
        Assert.Equal(edited.Title, workOrder.Title);
        Assert.Equal(edited.Description.Trim(), workOrder.Description);
        Assert.Equal(session.UserId, workOrder.AssignedToUserId);
        Assert.True(await db.OutboxEvents.AnyAsync(x => x.EntityId == workOrder.Id && x.EventType == "WorkOrderChanged"));
        Assert.True(await db.AuditLogs.AnyAsync(x => x.ResourceId == proposal.Id && x.Action == "Execute" && x.CorrelationId == receipt.ApprovalCorrelationId));
        Assert.True(await db.AuditLogs.AnyAsync(x => x.ResourceId == workOrder.Id && x.Action == "Create" && x.CorrelationId == receipt.ApprovalCorrelationId));
        await using var freshScope = session.Factory.Services.CreateAsyncScope();
        var fresh = await freshScope.ServiceProvider.GetRequiredService<IActionProposalService>().GetAsync(new(session.UserId, "after-restart"), proposal.Id);
        Assert.Equal(workOrder.Id, fresh.ResultEntityId);
        Assert.Equal(1, await db.AiRuns.CountAsync());
    }

    [DatabaseFact]
    public async Task ParallelApprovalsWithSameAndDifferentKeysCreateOneWorkOrderAndStaleHashCannotExecute()
    {
        var clock = new AlarmTests.Clock();
        await using var session = new AssetManagementTests.Session(clock, await IsolatedConnection());
        await session.Initialize();
        await session.Login();
        var run = await Investigate(session, clock);
        var proposal = await Propose(session, run.Id);
        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(index => session.Client.PostAsJsonAsync(Path(proposal.Id) + "/execute", Confirm(proposal, index % 2 == 0 ? "same" : "other"))));
        var receipts = new List<ActionProposalDto>();
        foreach (var response in responses)
        {
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            receipts.Add((await response.Content.ReadFromJsonAsync<ActionProposalDto>())!);
        }
        Assert.Single(receipts.Select(x => x.ResultEntityId).Distinct());
        Assert.Equal(HttpStatusCode.Conflict, (await session.Client.PostAsJsonAsync(Path(proposal.Id) + "/execute", Confirm(proposal) with
        {
            PayloadHash = new string('0', 64)
        })).StatusCode);
        await using var scope = session.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
        Assert.Equal(1, await db.WorkOrders.CountAsync(x => x.AiProposalId == proposal.Id));
        Assert.Equal(1, await db.AuditLogs.CountAsync(x => x.ResourceId == proposal.Id && x.Action == "Execute"));
    }

    [DatabaseFact]
    public async Task OldApprovalAfterEditRejectExpiryAndAlarmChangesCannotCreateWorkOrders()
    {
        var clock = new AlarmTests.Clock();
        await using var session = new AssetManagementTests.Session(clock, await IsolatedConnection());
        await session.Initialize();
        await session.Login();
        var run = await Investigate(session, clock);
        var edited = await Propose(session, run.Id);
        Assert.Equal(HttpStatusCode.OK, (await session.Client.PatchAsJsonAsync(Path(edited.Id), new EditActionProposal(edited.Version, edited.Draft! with
        {
            Title = "Changed"
        }))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await session.Client.PostAsJsonAsync(Path(edited.Id) + "/execute", Confirm(edited))).StatusCode);
        var rejected = await Propose(session, run.Id);
        Assert.Equal(HttpStatusCode.OK, (await session.Client.PostAsJsonAsync(Path(rejected.Id) + "/reject", new RejectActionProposal(rejected.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await session.Client.PostAsJsonAsync(Path(rejected.Id) + "/execute", Confirm(rejected))).StatusCode);
        var expired = await Propose(session, run.Id);
        clock.Now = clock.Now.AddMinutes(16);
        Assert.Equal(HttpStatusCode.Conflict, (await session.Client.PostAsJsonAsync(Path(expired.Id) + "/execute", Confirm(expired))).StatusCode);
        Assert.Equal("Expired", (await session.Client.GetFromJsonAsync<ActionProposalDto>(Path(expired.Id)))!.Status);
        var changed = await Propose(session, run.Id);
        var alarm = (await session.Client.GetFromJsonAsync<AlarmDto>($"/api/v1/alarms/{run.AlarmId}"))!;
        Assert.Equal(HttpStatusCode.OK, (await session.Client.PostAsJsonAsync($"/api/v1/alarms/{alarm.Id}/acknowledge", new
        {
            alarm.Version
        })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await session.Client.PostAsJsonAsync(Path(changed.Id) + "/execute", Confirm(changed))).StatusCode);
        Assert.Equal("Invalidated", (await session.Client.GetFromJsonAsync<ActionProposalDto>(Path(changed.Id)))!.Status);
        await using var scope = session.Factory.Services.CreateAsyncScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>().WorkOrders.ToListAsync());
    }

    [DatabaseFact]
    public async Task OwnerRoleCsrfAndUnknownPayloadFieldsAreEnforced()
    {
        var clock = new AlarmTests.Clock();
        var connection = await IsolatedConnection();
        await using var owner = new AssetManagementTests.Session(clock, connection);
        await owner.Initialize();
        await owner.Login();
        var run = await Investigate(owner, clock);
        var proposal = await Propose(owner, run.Id);
        await using var other = new AssetManagementTests.Session(clock, connection);
        await other.Initialize();
        await other.Login();
        Assert.Equal(HttpStatusCode.NotFound, (await other.Client.GetAsync(Path(proposal.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Client.PostAsJsonAsync(Path(proposal.Id) + "/execute", Confirm(proposal))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Client.PostAsync($"/api/v1/investigations/{run.Id}/proposals", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.Client.PostAsJsonAsync(Path(proposal.Id) + "/execute", new
        {
            proposal.Version,
            proposal.PayloadHash,
            IdempotencyKey = "forge",
            AssetId = Guid.NewGuid()
        })).StatusCode);
        owner.Client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.Client.PostAsJsonAsync(Path(proposal.Id) + "/execute", Confirm(proposal))).StatusCode);
        await owner.RefreshToken();
        await using var scope = owner.Factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<PlantOpsUser>>();
        var user = (await users.FindByIdAsync(owner.UserId))!;
        await users.RemoveFromRoleAsync(user, "Admin");
        await users.AddToRoleAsync(user, "Viewer");
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.Client.PostAsJsonAsync(Path(proposal.Id) + "/execute", Confirm(proposal))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.Client.PostAsync($"/api/v1/investigations/{run.Id}/proposals", null)).StatusCode);
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>().WorkOrders.ToListAsync());
    }

    [DatabaseFact]
    public async Task ChangedDocumentHidesDraftAndBlocksApprovalButPreservesAnExecutedReceipt()
    {
        var clock = new AlarmTests.Clock();
        await using var session = new AssetManagementTests.Session(clock, await IsolatedConnection());
        await session.Initialize();
        await session.Login();
        var run = await Investigate(session, clock, true);
        var executed = await Propose(session, run.Id);
        Assert.Equal(HttpStatusCode.OK, (await session.Client.PostAsJsonAsync(Path(executed.Id) + "/execute", Confirm(executed))).StatusCode);
        var pending = await Propose(session, run.Id);
        var document = run.Evidence.First(x => x.Kind == "Document");
        await using var scope = session.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
        await db.Documents.Where(x => x.Id == document.ResourceId).ExecuteUpdateAsync(update => update.SetProperty(x => x.CurrentPublishedVersionId, (Guid?)null));
        var hidden = (await session.Client.GetFromJsonAsync<ActionProposalDto>(Path(pending.Id)))!;
        Assert.Equal("Invalidated", hidden.Status);
        Assert.Null(hidden.Draft);
        Assert.Null(hidden.Investigation);
        Assert.Equal(HttpStatusCode.Conflict, (await session.Client.PostAsJsonAsync(Path(pending.Id) + "/execute", Confirm(pending))).StatusCode);
        var receipt = (await session.Client.GetFromJsonAsync<ActionProposalDto>(Path(executed.Id)))!;
        Assert.Equal("Executed", receipt.Status);
        Assert.NotNull(receipt.ResultEntityId);
        Assert.Null(receipt.Draft);
        Assert.Null(receipt.Investigation);
        Assert.Equal(1, await db.WorkOrders.CountAsync());
    }

    [DatabaseFact]
    public async Task FailureAfterSqlRollsBackProposalWorkOrderAuditAndOutboxAndRetryIsSafe()
    {
        var clock = new AlarmTests.Clock();
        await using var session = new AssetManagementTests.Session(clock, await IsolatedConnection());
        await session.Initialize();
        await session.Login();
        var run = await Investigate(session, clock);
        var proposal = await Propose(session, run.Id);
        session.Failure.Enabled = true;
        Assert.Equal(HttpStatusCode.InternalServerError, (await session.Client.PostAsJsonAsync(Path(proposal.Id) + "/execute", Confirm(proposal))).StatusCode);
        session.Failure.Enabled = false;
        await using var scope = session.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
        Assert.Empty(await db.WorkOrders.ToListAsync());
        Assert.False(await db.AuditLogs.AnyAsync(x => x.Action == "Execute"));
        Assert.False(await db.OutboxEvents.AnyAsync(x => x.EventType == "WorkOrderChanged"));
        Assert.Equal("Pending", (await session.Client.GetFromJsonAsync<ActionProposalDto>(Path(proposal.Id)))!.Status);
        Assert.Equal(HttpStatusCode.OK, (await session.Client.PostAsJsonAsync(Path(proposal.Id) + "/execute", Confirm(proposal))).StatusCode);
        Assert.Equal(1, await db.WorkOrders.CountAsync());
    }

    [DatabaseFact]
    public async Task PendingInvestigationInvalidAssigneeAndTamperedSourceCannotBeExecuted()
    {
        var clock = new AlarmTests.Clock();
        await using var session = new AssetManagementTests.Session(clock, await IsolatedConnection());
        await session.Initialize();
        await session.Login();
        var run = await Investigate(session, clock);
        var proposal = await Propose(session, run.Id);
        Assert.Equal(HttpStatusCode.BadRequest, (await session.Client.PatchAsJsonAsync(Path(proposal.Id),
            new EditActionProposal(proposal.Version, proposal.Draft! with
            {
                AssignedToUserId = "missing-user"
            }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await session.Client.PatchAsJsonAsync(Path(proposal.Id),
            new EditActionProposal(proposal.Version, proposal.Draft! with
            {
                Title = " "
            }))).StatusCode);
        await using var scope = session.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
        await db.AiRuns.Where(x => x.Id == run.Id).ExecuteUpdateAsync(update => update.SetProperty(x => x.WarningsJson, "[\"changed evidence warning\"]"));
        Assert.Equal(HttpStatusCode.Conflict, (await session.Client.PostAsJsonAsync(Path(proposal.Id) + "/execute", Confirm(proposal))).StatusCode);
        await db.AiRuns.Where(x => x.Id == run.Id).ExecuteUpdateAsync(update => update.SetProperty(x => x.Status, "Pending").SetProperty(x => x.ResultJson, (string?)null));
        Assert.Equal(HttpStatusCode.Conflict, (await session.Client.PostAsync($"/api/v1/investigations/{run.Id}/proposals", null)).StatusCode);
        Assert.Empty(await db.WorkOrders.ToListAsync());
    }

    [DatabaseFact]
    public async Task RevocationAndResourceChangesWhileApprovalWaitsForPlantLockAreRechecked()
    {
        foreach (var change in new[] { "role", "scope", "disabled", "asset", "document" })
        {
            var clock = new AlarmTests.Clock();
            await using var session = new AssetManagementTests.Session(clock, await IsolatedConnection());
            await session.Initialize();
            await session.Login();
            var run = await Investigate(session, clock, change == "document");
            var proposal = await Propose(session, run.Id);
            await using var scope = session.Factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({proposal.PlantId.ToString()}, 0))");
            var executing = session.Client.PostAsJsonAsync(Path(proposal.Id) + "/execute", Confirm(proposal));
            var waiting = false;
            for (var attempt = 0; attempt < 100 && !executing.IsCompleted; attempt++)
            {
                waiting = await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM pg_locks WHERE locktype='advisory' AND NOT granted AND database=(SELECT oid FROM pg_database WHERE datname=current_database())").SingleAsync() > 0;
                if (waiting)
                {
                    break;
                }
                await Task.Delay(20);
            }
            Assert.True(waiting, "Approval should be waiting for the plant lock before revocation: " + change);
            switch (change)
            {
                case "role":
                    await db.UserRoles.Where(x => x.UserId == session.UserId).ExecuteDeleteAsync();
                    break;
                case "scope":
                    await db.UserPlantAccess.Where(x => x.UserId == session.UserId).ExecuteDeleteAsync();
                    break;
                case "disabled":
                    await db.Users.Where(x => x.Id == session.UserId).ExecuteUpdateAsync(update => update.SetProperty(x => x.IsEnabled, false));
                    break;
                case "asset":
                    await db.Assets.Where(x => x.Id == proposal.AssetId).ExecuteUpdateAsync(update => update.SetProperty(x => x.Version, x => x.Version + 1));
                    break;
                case "document":
                    var document = run.Evidence.First(x => x.Kind == "Document");
                    await db.Documents.Where(x => x.Id == document.ResourceId).ExecuteUpdateAsync(update => update.SetProperty(x => x.AllowedRoles, new[] { "MaintenanceEngineer" }).SetProperty(x => x.Visibility, "RestrictedRoles"));
                    break;
            }
            await transaction.CommitAsync();
            var response = await executing;
            var expected = change switch
            {
                "role" => HttpStatusCode.Forbidden,
                "scope" => HttpStatusCode.NotFound,
                "disabled" => HttpStatusCode.Unauthorized,
                _ => HttpStatusCode.Conflict
            };
            Assert.Equal(expected, response.StatusCode);
            Assert.Empty(await db.WorkOrders.ToListAsync());
        }
    }

    [DatabaseFact]
    public async Task CompetingEditRejectAndExecuteHaveOneWinnerAndNeverExecuteEditedContents()
    {
        var clock = new AlarmTests.Clock();
        await using var session = new AssetManagementTests.Session(clock, await IsolatedConnection());
        await session.Initialize();
        await session.Login();
        var run = await Investigate(session, clock);
        var proposal = await Propose(session, run.Id);
        var responses = await Task.WhenAll(
            session.Client.PostAsJsonAsync(Path(proposal.Id) + "/execute", Confirm(proposal)),
            session.Client.PatchAsJsonAsync(Path(proposal.Id), new EditActionProposal(proposal.Version, proposal.Draft! with
            {
                Title = "Competing edit"
            })),
            session.Client.PostAsJsonAsync(Path(proposal.Id) + "/reject", new RejectActionProposal(proposal.Version)));
        Assert.Single(responses, response => response.IsSuccessStatusCode);
        Assert.Equal(2, responses.Count(response => response.StatusCode == HttpStatusCode.Conflict));
        var current = (await session.Client.GetFromJsonAsync<ActionProposalDto>(Path(proposal.Id)))!;
        await using var scope = session.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
        var orders = await db.WorkOrders.ToListAsync();
        if (current.Status == "Executed")
        {
            Assert.Equal(proposal.Draft!.Title, Assert.Single(orders).Title);
        }
        else
        {
            Assert.Empty(orders);
        }
    }

    [DatabaseFact]
    public async Task EvidenceRevocationCannotCrossTheRevalidationToCommitWindow()
    {
        var clock = new AlarmTests.Clock();
        var gate = new PauseApprovalCommit();
        await using var session = new AssetManagementTests.Session(clock, await IsolatedConnection(),
            services => services.AddDbContext<PlantOpsDbContext>(options => options.AddInterceptors(gate)));
        await session.Initialize();
        await session.Login();
        var run = await Investigate(session, clock, true);
        var proposal = await Propose(session, run.Id);
        var document = run.Evidence.First(x => x.Kind == "Document");
        gate.Enabled = true;
        var execution = session.Client.PostAsJsonAsync(Path(proposal.Id) + "/execute", Confirm(proposal));
        try
        {
            await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await using var writer = session.Factory.Services.CreateAsyncScope();
            var writeDb = writer.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
            var revoking = writeDb.Documents.Where(x => x.Id == document.ResourceId)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.CurrentPublishedVersionId, (Guid?)null));
            await using var observer = session.Factory.Services.CreateAsyncScope();
            var observeDb = observer.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
            var waiting = false;
            for (var attempt = 0; attempt < 100 && !revoking.IsCompleted; attempt++)
            {
                waiting = await observeDb.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM pg_stat_activity WHERE datname=current_database() AND wait_event_type='Lock' AND query LIKE 'UPDATE \"Documents\"%'").SingleAsync() > 0;
                if (waiting)
                {
                    break;
                }
                await Task.Delay(20);
            }
            Assert.True(waiting, "Document revocation must wait until approval commits or rolls back.");
            gate.Resume.TrySetResult();
            Assert.Equal(HttpStatusCode.OK, (await execution).StatusCode);
            await revoking;
            var receipt = (await session.Client.GetFromJsonAsync<ActionProposalDto>(Path(proposal.Id)))!;
            Assert.Equal("Executed", receipt.Status);
            Assert.Null(receipt.Draft);
            Assert.NotNull(receipt.ResultEntityId);
            Assert.Equal(1, await observeDb.WorkOrders.CountAsync(x => x.AiProposalId == proposal.Id));
        }
        finally
        {
            gate.Resume.TrySetResult();
            await execution;
        }
    }

    [DatabaseFact]
    public async Task ExpiryDuringTheFinalCommitWindowRollsBackTheWorkOrder()
    {
        var clock = new AlarmTests.Clock();
        var gate = new PauseApprovalCommit();
        await using var session = new AssetManagementTests.Session(clock, await IsolatedConnection(),
            services => services.AddDbContext<PlantOpsDbContext>(options => options.AddInterceptors(gate)));
        await session.Initialize();
        await session.Login();
        var run = await Investigate(session, clock);
        var proposal = await Propose(session, run.Id);
        gate.Enabled = true;
        var execution = session.Client.PostAsJsonAsync(Path(proposal.Id) + "/execute", Confirm(proposal));
        try
        {
            await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            clock.Now = clock.Now.AddMinutes(16);
        }
        finally
        {
            gate.Resume.TrySetResult();
        }
        Assert.Equal(HttpStatusCode.Conflict, (await execution).StatusCode);
        var current = (await session.Client.GetFromJsonAsync<ActionProposalDto>(Path(proposal.Id)))!;
        Assert.Equal("Expired", current.Status);
        Assert.Null(current.ResultEntityId);
        await using var scope = session.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
        Assert.Empty(await db.WorkOrders.ToListAsync());
        Assert.False(await db.AuditLogs.AnyAsync(x => x.ResourceId == proposal.Id && x.Action == "Execute"));
    }
}
