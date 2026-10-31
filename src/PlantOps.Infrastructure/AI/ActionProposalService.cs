using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PlantOps.Application.AI;
using PlantOps.Application.Assets;
using PlantOps.Application.Maintenance;
using PlantOps.Domain.AI;
using PlantOps.Domain.Identity;
using PlantOps.Infrastructure.Maintenance;
using PlantOps.Infrastructure.Persistence;

namespace PlantOps.Infrastructure.AI;

public sealed class ActionProposalService(PlantOpsDbContext db, IInvestigationService investigations,
    WorkOrderService workOrders, TimeProvider clock) : IActionProposalService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static string Hash(object value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, Json)));

    private static string InvestigationHash(AiRun run) => Hash(new { run.ResultJson, run.EvidenceJson, run.WarningsJson, run.Provider, run.Model, run.Simulated });

    private static WorkOrderDraft Payload(ActionProposal proposal) => JsonSerializer.Deserialize<WorkOrderDraft>(proposal.PayloadJson, Json)!;

    private static WorkOrderNoteDraft NotePayload(ActionProposal proposal) => JsonSerializer.Deserialize<WorkOrderNoteDraft>(proposal.PayloadJson, Json)!;

    private static string NotePayloadHash(ActionProposal proposal, WorkOrderNoteDraft draft) => Hash(new
    {
        proposal.SchemaVersion,
        proposal.ActionType,
        proposal.DraftOrigin,
        proposal.AiRunId,
        proposal.PlantId,
        proposal.AssetId,
        proposal.AlarmId,
        proposal.AssetVersion,
        proposal.AlarmVersion,
        proposal.InvestigationHash,
        proposal.TargetWorkOrderId,
        proposal.TargetWorkOrderVersion,
        draft
    });

    private static string PayloadHash(ActionProposal proposal, WorkOrderDraft draft) =>
        Hash(new
        {
            proposal.SchemaVersion,
            proposal.ActionType,
            proposal.DraftOrigin,
            proposal.AiRunId,
            proposal.PlantId,
            proposal.AssetId,
            proposal.AlarmId,
            proposal.AssetVersion,
            proposal.AlarmVersion,
            proposal.InvestigationHash,
            draft
        });

    private async Task<AiRun> OwnedRunAsync(Actor actor, Guid runId, CancellationToken token) =>
        await db.AiRuns.AsNoTracking().SingleOrDefaultAsync(x => x.Id == runId && x.ActorUserId == actor.UserId, token)
        ?? throw new DirectoryException(404, "Investigation not found.");

    private async Task<ActionProposal> OwnedAsync(Actor actor, Guid id, CancellationToken token) =>
        await db.ActionProposals.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.OwnerUserId == actor.UserId, token)
        ?? throw new DirectoryException(404, "Proposal not found.");

    private async Task LockPlantAsync(Guid plantId, CancellationToken token)
    {
        // A bounded wait returns a recoverable error; no model or network work happens in this transaction.
        await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '5s'", token);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({plantId.ToString()}, 0))", token);
    }

    private async Task LockIdentityAsync(string userId, Guid plantId, CancellationToken token)
    {
        // Share row locks prevent revocation/disable between authorization and commit, even outside plant-lock writers.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"AspNetUsers\" WHERE \"Id\" = {userId} FOR SHARE", token);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"AspNetUserRoles\" WHERE \"UserId\" = {userId} ORDER BY \"RoleId\" FOR SHARE", token);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"AspNetRoles\" WHERE \"Id\" IN (SELECT \"RoleId\" FROM \"AspNetUserRoles\" WHERE \"UserId\" = {userId}) ORDER BY \"Id\" FOR SHARE", token);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"UserPlantAccess\" WHERE \"UserId\" = {userId} AND \"PlantId\" = {plantId} FOR SHARE", token);
    }

    private async Task AuthorizeAsync(Actor actor, Guid plantId, Guid assetId, bool maintenance, CancellationToken token)
    {
        if (!await db.Users.AsNoTracking().AnyAsync(x => x.Id == actor.UserId && x.IsEnabled, token))
        {
            throw new DirectoryException(401, "Session unavailable.");
        }
        if (!await db.UserPlantAccess.AnyAsync(x => x.UserId == actor.UserId && x.PlantId == plantId, token)
            || !await db.Assets.AsNoTracking().AnyAsync(x => x.Id == assetId && x.PlantId == plantId && !x.IsArchived, token)
            || !await db.Plants.AsNoTracking().AnyAsync(x => x.Id == plantId && !x.IsArchived, token))
        {
            throw new DirectoryException(404, "Proposal not available.");
        }
        var roles = await (from grant in db.UserRoles
                           join role in db.Roles on grant.RoleId equals role.Id
                           where grant.UserId == actor.UserId
                           select role.Name).ToListAsync(token);
        if (maintenance && !roles.Any(x => x is "Admin" or "MaintenanceEngineer")
            || !roles.Any(x => x is "Admin" or "MaintenanceEngineer" or "Viewer" or "Operator"))
        {
            throw new DirectoryException(403, "Maintenance role required.");
        }
    }

    private async Task<AiRun> LockContextAsync(Actor actor, ActionProposal proposal, bool maintenance, CancellationToken token)
    {
        var run = await OwnedRunAsync(actor, proposal.AiRunId, token);
        await LockIdentityAsync(actor.UserId, proposal.PlantId, token);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Plants\" WHERE \"Id\" = {proposal.PlantId} FOR SHARE", token);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Assets\" WHERE \"Id\" = {proposal.AssetId} FOR SHARE", token);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Alarms\" WHERE \"Id\" = {proposal.AlarmId} FOR SHARE", token);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"AiRuns\" WHERE \"Id\" = {run.Id} FOR SHARE", token);
        await AuthorizeAsync(actor, proposal.PlantId, proposal.AssetId, maintenance, token);
        if (proposal.TargetWorkOrderId is { } orderId)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"WorkOrders\" WHERE \"Id\" = {orderId} AND \"PlantId\" = {proposal.PlantId} AND \"AssetId\" = {proposal.AssetId} FOR UPDATE", token);
        }
        foreach (var item in (JsonSerializer.Deserialize<InvestigationEvidence[]>(run.EvidenceJson, Json) ?? [])
            .Where(x => x.Kind == "Document").OrderBy(x => x.ResourceId).ThenBy(x => x.VersionId).ThenBy(x => x.ChunkId))
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Documents\" WHERE \"Id\" = {item.ResourceId} FOR SHARE", token);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"DocumentVersions\" WHERE \"Id\" = {item.VersionId} FOR SHARE", token);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"DocumentChunks\" WHERE \"Id\" = {item.ChunkId} FOR SHARE", token);
        }
        return await OwnedRunAsync(actor, run.Id, token);
    }

    private async Task<ActionProposal> LockProposalAsync(Actor actor, Guid id, CancellationToken token)
    {
        var snapshot = await OwnedAsync(actor, id, token);
        await LockPlantAsync(snapshot.PlantId, token);
        // Avoid EF identity-map snapshots after waiting for another approval/editor.
        var tracked = db.ActionProposals.Local.SingleOrDefault(x => x.Id == id);
        if (tracked != null)
        {
            db.Entry(tracked).State = EntityState.Detached;
        }
        return await db.ActionProposals.FromSqlInterpolated($"SELECT * FROM \"ActionProposals\" WHERE \"Id\" = {id} AND \"OwnerUserId\" = {actor.UserId} FOR UPDATE")
            .SingleAsync(token);
    }

    private async Task<InvestigationRunDto?> CurrentInvestigationAsync(Actor actor, ActionProposal proposal, AiRun run, CancellationToken token)
    {
        if (run.Status != "Succeeded" || run.ResultJson == null || InvestigationHash(run) != proposal.InvestigationHash)
        {
            return null;
        }
        try
        {
            var current = await investigations.GetAsync(actor, run.Id, token);
            return current.Status == "Succeeded" && current.Result != null ? current : null;
        }
        catch (DirectoryException exception) when (exception.Status is 404 or 409)
        {
            return null;
        }
    }

    private async Task<ActionProposalDto> DtoAsync(Actor actor, ActionProposal proposal, AiRun run, CancellationToken token)
    {
        var investigation = await CurrentInvestigationAsync(actor, proposal, run, token);
        var asset = await db.Assets.AsNoTracking().SingleAsync(x => x.Id == proposal.AssetId, token);
        var alarm = await db.Alarms.AsNoTracking().SingleAsync(x => x.Id == proposal.AlarmId, token);
        var status = proposal.Status;
        var target = proposal.TargetWorkOrderId.HasValue
            ? await db.WorkOrders.AsNoTracking().SingleOrDefaultAsync(x => x.Id == proposal.TargetWorkOrderId
                && x.AssetId == proposal.AssetId && x.PlantId == proposal.PlantId, token) : null;
        string? reason = null;
        if (investigation == null)
        {
            reason = "资料版本或权限已变化，旧提案正文已隐藏。请重新调查。";
            if (status == "Pending")
            {
                status = "Invalidated";
            }
        }
        if (status == "Pending" && (asset.Version != proposal.AssetVersion || alarm.Version != proposal.AlarmVersion))
        {
            status = "Invalidated";
            reason = "设备或告警已变化，请重新整理并确认工单草稿。";
        }
        if (status == "Pending" && clock.GetUtcNow() >= proposal.ExpiresAt)
        {
            status = "Expired";
            reason = "提案已过期，请重新整理并确认工单草稿。";
        }
        if (status == "Pending" && proposal.ActionType == "AppendWorkOrderNote"
            && (target == null || target.Version != proposal.TargetWorkOrderVersion || target.Status is "Closed" or "Cancelled"))
        {
            status = "Invalidated";
            reason = "目标工单已变化、关闭或取消，请重新选择工单并整理备注。";
        }
        if (proposal.Status == "Pending" && status != "Pending")
        {
            var before = JsonSerializer.Serialize(proposal, Json);
            proposal.Status = status;
            proposal.Version++;
            Audit(actor, proposal, status, before);
            await db.SaveChangesAsync(token);
        }
        return new(proposal.Id, proposal.AiRunId, proposal.PlantId, proposal.AssetId, proposal.AlarmId,
            asset.Name, status, proposal.Version, proposal.PayloadHash, proposal.ExpiresAt,
            investigation != null && status is not ("Invalidated" or "Expired") && proposal.ActionType == "CreateWorkOrder" ? Payload(proposal) : null,
            investigation, proposal.ResultEntityId, proposal.ExecutedAt, proposal.ApprovalCorrelationId, reason,
            proposal.ActionType, proposal.TargetWorkOrderId, proposal.TargetWorkOrderVersion, target?.Number, target?.Title,
            investigation != null && status is not ("Invalidated" or "Expired") && proposal.ActionType == "AppendWorkOrderNote" ? NotePayload(proposal) : null);
    }

    private void Audit(Actor actor, ActionProposal proposal, string action, string? before) => db.AuditLogs.Add(new AuditLog
    {
        PlantId = proposal.PlantId,
        ResourceId = proposal.Id,
        ResourceType = "ActionProposal",
        Action = action,
        ActorUserId = actor.UserId,
        Before = before,
        After = JsonSerializer.Serialize(proposal, Json),
        CorrelationId = actor.CorrelationId,
        OccurredAt = clock.GetUtcNow()
    });

    private static WorkOrderDraft Normalize(WorkOrderDraft draft)
    {
        if (string.IsNullOrWhiteSpace(draft.Title) || draft.Title.Length > 200 || draft.Description == null
            || draft.Description.Length > 8000 || draft.Priority is not ("Low" or "Medium" or "High")
            || draft.AssignedToUserId?.Length > 450)
        {
            throw new DirectoryException(400, "标题必填且最多200字，描述最多8000字，请选择有效优先级。生成内容超限时请缩减调查内容后重新整理。");
        }
        return draft with
        {
            Title = draft.Title.Trim(),
            Description = draft.Description.Trim(),
            AssignedToUserId = string.IsNullOrWhiteSpace(draft.AssignedToUserId) ? null : draft.AssignedToUserId.Trim()
        };
    }

    private static WorkOrderDraft AdviceTemplate(InvestigationRunDto run, bool requireDraft = true)
    {
        var advice = run.Result!.Value.Deserialize<InvestigationAdvice>(Json)!;
        if (requireDraft && (advice.WorkOrder.Status != "DraftReady" || advice.WorkOrder.Draft == null))
        {
            throw new DirectoryException(409, advice.WorkOrder.Reason);
        }
        var draft = InvestigationDraftContent.Build(advice, run.Language, requireDraft);
        var source = run.Simulated ? run.Language == "en" ? "Simulated model result" : "模拟模型结果" : run.Model;
        var prefix = run.Language == "en" ? $"Investigation {run.Id} · {run.CreatedAt:O}\nModel source: {source}\n"
            : $"调查 {run.Id} · {run.CreatedAt:O}\n模型来源：{source}\n";
        var sourceKinds = run.Evidence.Where(x => x.PointStatus != null).Select(x => x.PointStatus!.SourceKind)
            .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToArray();
        if (sourceKinds.Length > 0)
        {
            prefix += (run.Language == "en" ? "Observation sources: " : "观测来源：") + string.Join(" / ", sourceKinds) + "\n";
        }
        return Normalize(draft with
        {
            Description = prefix + draft.Description
        });
    }

    private static WorkOrderDraft Template(InvestigationRunDto run)
    {
        var result = run.Result!.Value;
        if (result.TryGetProperty("schemaVersion", out var schemaVersion) && schemaVersion.GetString() == "3")
        {
            return AdviceTemplate(run);
        }
        var english = run.Language == "en";
        string L(string value) => english ? InvestigationEnglish.Label(value) : value;
        string F(string kind, string value) => english ? InvestigationEnglish.Fact(kind, value) : value;
        string Prose(string value)
        {
            if (!english)
            {
                return value;
            }

            foreach (var evidence in run.Evidence.Where(x => x.Kind != "Document"))
            {
                foreach (var fact in evidence.Facts.OrderByDescending(x => x.Length))
                {
                    value = value.Replace(fact, F(evidence.Kind, fact), StringComparison.Ordinal);
                }
            }

            return value;
        }
        var asset = run.Evidence.FirstOrDefault(x => x.Kind == "Asset");
        var alarm = run.Evidence.FirstOrDefault(x => x.Kind == "Alarm");
        var text = new StringBuilder(english ? $"Investigation {run.Id} · {run.CreatedAt:O}\nModel source: {(run.Simulated ? "Simulated model result" : run.Model)}\nDraft for human review; maintenance personnel must verify inspection results and field procedures.\n" : $"调查 {run.Id} · {run.CreatedAt:O}\n模型来源：{(run.Simulated ? L("模拟模型结果") : run.Model)}\n人工审核草稿；检查结果与现场规程由维护人员核实。\n");
        text.AppendLine(("\n" + L("对象与事件摘要")));
        foreach (var item in new[] { asset, alarm }.Where(x => x != null))
        {
            foreach (var fact in item!.Facts.Take(2))
            {
                text.AppendLine($"[{item.Id}] {F(item.Kind, fact)}");
            }
        }
        foreach (var comparison in run.Evidence.Where(x => x.Kind == "RelatedComparison"))
        {
            text.AppendLine(("\n" + L("已自动读取的关联趋势（调查时快照）")));
            foreach (var fact in comparison.Facts)
            {
                text.AppendLine($"[{comparison.Id}] {F(comparison.Kind, fact)}");
            }
        }
        foreach (var assessment in run.Evidence.Where(x => x.Kind == "Assessment"))
        {
            text.AppendLine(("\n" + L("数据可信度与设备异常证据（截至取证时间）")));
            foreach (var fact in assessment.Facts)
            {
                text.AppendLine($"[{assessment.Id}] {F(assessment.Kind, fact)}");
            }
            foreach (var status in run.Evidence.Where(x => x.Kind == "PointStatus").GroupBy(x => x.ResourceId).Select(x => x.Last()))
            {
                text.AppendLine($"[{status.Id}] {F(status.Kind, status.Facts[0])}");
            }
        }
        if (result.TryGetProperty("analysis", out var analysis) && analysis.ValueKind == JsonValueKind.Array && analysis.GetArrayLength() > 0)
        {
            text.AppendLine(("\n" + L("趋势分析（待人工核查）")));
            foreach (var item in analysis.EnumerateArray())
            {
                text.AppendLine(string.Join(" ", item.GetProperty("evidenceIds").EnumerateArray().Select(x => $"[{x.GetString()}]"))
                    + " " + item.GetProperty("text").GetString());
            }
        }
        text.AppendLine(("\n" + L("按序检查与记录")));
        var index = 0;
        foreach (var check in result.GetProperty("nextChecks").EnumerateArray())
        {
            text.AppendLine($"{++index}. {Prose(check.GetProperty("text").GetString()!)}");
            var ids = check.GetProperty("evidenceIds").EnumerateArray().Select(x => x.GetString()).ToHashSet();
            text.AppendLine(L("依据：") + string.Join("；", run.Evidence.Where(x => ids.Contains(x.Id)).Select(x => $"[{x.Id}] {(english ? InvestigationEnglish.Title(x.Kind, x.Title) : x.Title)}")));
            string Field(string key, string fallback) => check.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
                ? Prose(value.GetString()!) : fallback;
            text.AppendLine(L("需记录：") + Field("record", L("检查时间、人员、点位/工况、观察值及单位、质量与来源、支持或排除的证据；资料不适用或未取得时明确记录。")));
            text.AppendLine(L("本项完成条件：") + Field("completion", L("补全检查证据并由维护工程师复核。")));
            text.AppendLine(L("本项升级条件：") + Field("escalation", L("证据不足、资料不适用或异常复发时交维护工程师复核。")));
        }
        if (index == 0)
        {
            text.AppendLine(L("尚无有依据的检查项；由维护人员补充适用资料并审核后再安排。"));
        }
        text.AppendLine(("\n" + L("完成与升级条件")));
        text.AppendLine(L("逐项填写证据及结论，区分历史事件、恢复记录和当前观测；核实资料版本及适用范围。告警恢复本身不等于工单完成或根因消除。"));
        text.AppendLine(L("事件证据不足、再次异常、观测不可信或资料不适用时，记录缺项并交维护工程师复核；不得凭本草稿执行设备控制、修改阈值或改变告警状态。"));
        var missing = result.GetProperty("missingInformation").EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct();
        text.AppendLine(("\n" + L("待补证据：")) + string.Join(english ? "; " : "；", missing.Select(x => Prose(x!))));
        var quality = run.Evidence.Where(x => x.Kind == "DataQuality").SelectMany(x => x.Facts).Concat(run.Warnings).Distinct(StringComparer.Ordinal).ToArray();
        if (quality.Length > 0)
        {
            text.AppendLine(("\n" + L("质量与来源提示（去重）")));
            foreach (var warning in quality.Take(8))
            {
                text.AppendLine(F("DataQuality", warning));
            }
            if (quality.Length > 8)
            {
                text.AppendLine(L("其余提示见完整调查证据台账。"));
            }
        }
        text.AppendLine(english ? $"\nComplete facts, hypotheses to verify and all citations: investigation {run.Id} (workbench investigation details; original evidence can be expanded)." : $"\n完整事实、待验证假设及全部引用：调查 {run.Id}（工作台调查详情，可展开原始证据）。");
        return Normalize(new(L("核查告警事件并记录排查证据"), text.ToString()));
    }

    public Task<ActionProposalDto> CreateAsync(Actor actor, Guid runId, CancellationToken cancellationToken = default) =>
        CreateCoreAsync(actor, runId, null, cancellationToken);

    public Task<ActionProposalDto> CreateNoteAsync(Actor actor, Guid runId, CreateNoteProposal input, CancellationToken cancellationToken = default) =>
        CreateCoreAsync(actor, runId, input, cancellationToken);

    private async Task<ActionProposalDto> CreateCoreAsync(Actor actor, Guid runId, CreateNoteProposal? input, CancellationToken cancellationToken)
    {
        var run = await OwnedRunAsync(actor, runId, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockPlantAsync(run.PlantId, cancellationToken);
        var proposal = new ActionProposal
        {
            AiRunId = runId,
            PlantId = run.PlantId,
            AssetId = run.AssetId,
            AlarmId = run.AlarmId,
            OwnerUserId = actor.UserId,
            CreatedAt = clock.GetUtcNow()
        };
        if (input != null)
        {
            proposal.ActionType = "AppendWorkOrderNote";
            proposal.SchemaVersion = 2;
            proposal.TargetWorkOrderId = input.WorkOrderId;
            proposal.TargetWorkOrderVersion = input.WorkOrderVersion;
        }
        run = await LockContextAsync(actor, proposal, true, cancellationToken);
        if (input != null)
        {
            var target = await db.WorkOrders.AsNoTracking().SingleOrDefaultAsync(x => x.Id == input.WorkOrderId
                && x.AssetId == proposal.AssetId && x.PlantId == proposal.PlantId, cancellationToken)
                ?? throw new DirectoryException(404, "目标工单不在当前设备下或不可用。");
            if (target.Version != input.WorkOrderVersion || target.Status is "Closed" or "Cancelled")
            {
                throw new DirectoryException(409, "目标工单已变化、关闭或取消，请重新选择。");
            }
        }
        var investigation = await investigations.GetAsync(actor, runId, cancellationToken);
        if (investigation.Status != "Succeeded" || investigation.Result == null)
        {
            throw new DirectoryException(409, "只有成功且当前仍可读取的调查才能整理工单草稿。");
        }
        proposal.ExpiresAt = proposal.CreatedAt.AddMinutes(15);
        proposal.AssetVersion = await db.Assets.Where(x => x.Id == run.AssetId).Select(x => x.Version).SingleAsync(cancellationToken);
        proposal.AlarmVersion = await db.Alarms.Where(x => x.Id == run.AlarmId).Select(x => x.Version).SingleAsync(cancellationToken);
        proposal.InvestigationHash = InvestigationHash(run);
        var isAdvice = investigation.Result.Value.TryGetProperty("schemaVersion", out var responseVersion) && responseVersion.GetString() == "3";
        // Storage origin denotes server-rendered investigation content; response schema/version is retained in the run hash.

        var draft = input != null && isAdvice
            ? AdviceTemplate(investigation, requireDraft: false)
            : Template(investigation);
        if (input == null)
        {
            proposal.PayloadJson = JsonSerializer.Serialize(draft, Json);
            proposal.PayloadHash = PayloadHash(proposal, draft);
        }
        else
        {
            var note = NormalizeNote(new(draft.Description));
            proposal.PayloadJson = JsonSerializer.Serialize(note, Json);
            proposal.PayloadHash = NotePayloadHash(proposal, note);
        }
        db.ActionProposals.Add(proposal);
        Audit(actor, proposal, "Create", null);
        await db.SaveChangesAsync(cancellationToken);
        var dto = await DtoAsync(actor, proposal, run, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return dto;
    }

    public async Task<IReadOnlyList<ActionProposalDto>> ListAsync(Actor actor, Guid runId, CancellationToken cancellationToken = default)
    {
        var run = await OwnedRunAsync(actor, runId, cancellationToken);
        await AuthorizeAsync(actor, run.PlantId, run.AssetId, false, cancellationToken);
        var ids = await db.ActionProposals.AsNoTracking().Where(x => x.AiRunId == runId && x.OwnerUserId == actor.UserId)
            .OrderBy(x => x.Status == "Pending" ? 0 : 1).ThenByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            .Take(20).Select(x => x.Id).ToListAsync(cancellationToken);
        var result = new List<ActionProposalDto>();
        foreach (var id in ids)
        {
            result.Add(await GetAsync(actor, id, cancellationToken));
        }
        return result.OrderBy(x => x.Status == "Pending" ? 0 : 1).ToArray();
    }

    public async Task<ActionProposalDto> GetAsync(Actor actor, Guid id, CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var proposal = await LockProposalAsync(actor, id, cancellationToken);
        var run = await LockContextAsync(actor, proposal, false, cancellationToken);
        var dto = await DtoAsync(actor, proposal, run, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return dto;
    }

    private async Task<ActionProposalDto> MutateAsync(Actor actor, Guid id, long version,
        Func<ActionProposal, Task> change, CancellationToken token)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var proposal = await LockProposalAsync(actor, id, token);
        var run = await LockContextAsync(actor, proposal, true, token);
        var current = await DtoAsync(actor, proposal, run, token);
        if (current.Status != "Pending" || proposal.Version != version)
        {
            // Persist terminal invalidation even though the requested command cannot proceed.
            await transaction.CommitAsync(token);
            throw new DirectoryException(409, current.UnavailableReason ?? "提案状态或版本已变化，请刷新并重新核对。");
        }
        await change(proposal);
        await db.SaveChangesAsync(token);
        var result = await DtoAsync(actor, proposal, run, token);
        await transaction.CommitAsync(token);
        return result;
    }

    public Task<ActionProposalDto> EditAsync(Actor actor, Guid id, EditActionProposal input, CancellationToken cancellationToken = default) =>
        MutateAsync(actor, id, input.Version, async proposal =>
        {
            if (proposal.ActionType != "CreateWorkOrder")
            {
                throw new DirectoryException(400, "提案类型不匹配。");
            }
            var draft = Normalize(input.Draft ?? throw new DirectoryException(400, "Draft required."));
            await ValidateAssigneeAsync(actor, proposal.PlantId, draft.AssignedToUserId, cancellationToken);
            var before = JsonSerializer.Serialize(proposal, Json);
            proposal.PayloadJson = JsonSerializer.Serialize(draft, Json);
            proposal.PayloadHash = PayloadHash(proposal, draft);
            proposal.Version++;
            Audit(actor, proposal, "Edit", before);
        }, cancellationToken);

    private static WorkOrderNoteDraft NormalizeNote(WorkOrderNoteDraft draft)
    {
        if (string.IsNullOrWhiteSpace(draft.Text) || draft.Text.Length > 8000)
        {
            throw new DirectoryException(400, "备注必填且最多8000字。");
        }
        return draft with
        {
            Text = draft.Text.Trim()
        };
    }

    public Task<ActionProposalDto> EditNoteAsync(Actor actor, Guid id, EditNoteProposal input, CancellationToken cancellationToken = default) =>
        MutateAsync(actor, id, input.Version, proposal =>
        {
            if (proposal.ActionType != "AppendWorkOrderNote")
            {
                throw new DirectoryException(400, "提案类型不匹配。");
            }
            var draft = NormalizeNote(input.Draft ?? throw new DirectoryException(400, "Draft required."));
            var before = JsonSerializer.Serialize(proposal, Json);
            proposal.PayloadJson = JsonSerializer.Serialize(draft, Json);
            proposal.PayloadHash = NotePayloadHash(proposal, draft);
            proposal.Version++;
            Audit(actor, proposal, "Edit", before);
            return Task.CompletedTask;
        }, cancellationToken);

    public Task<ActionProposalDto> RejectAsync(Actor actor, Guid id, RejectActionProposal input, CancellationToken cancellationToken = default) =>
        MutateAsync(actor, id, input.Version, proposal =>
        {
            var before = JsonSerializer.Serialize(proposal, Json);
            proposal.Status = "Rejected";
            proposal.Version++;
            Audit(actor, proposal, "Reject", before);
            return Task.CompletedTask;
        }, cancellationToken);

    private async Task ValidateAssigneeAsync(Actor actor, Guid plantId, string? userId, CancellationToken token)
    {
        if (userId == null)
        {
            return;
        }
        await LockIdentityAsync(userId, plantId, token);
        if (!(await workOrders.AssigneesAsync(actor, plantId)).Any(x => x.Id == userId))
        {
            throw new DirectoryException(400, "责任人需要具备当前工厂的维护权限。");
        }
    }

    private void RequireUnexpired(ActionProposal proposal)
    {
        if (clock.GetUtcNow() >= proposal.ExpiresAt)
        {
            throw new DirectoryException(409, "提案已过期，请重新整理并确认工单草稿。");
        }
    }

    public async Task<ActionProposalDto> ExecuteAsync(Actor actor, Guid id, ConfirmActionProposal input, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(input.IdempotencyKey) || input.IdempotencyKey.Length > 128 || input.PayloadHash?.Length != 64)
        {
            throw new DirectoryException(400, "A bounded idempotency key and payload hash are required.");
        }
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var proposal = await LockProposalAsync(actor, id, cancellationToken);
        var run = await LockContextAsync(actor, proposal, true, cancellationToken);
        if (proposal.Version != input.Version || proposal.PayloadHash != input.PayloadHash)
        {
            throw new DirectoryException(409, "提案内容已变化，请重新保存并核对完整内容。");
        }
        if (proposal.Status == "Executed")
        {
            var receipt = await DtoAsync(actor, proposal, run, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return receipt;
        }
        var current = await DtoAsync(actor, proposal, run, cancellationToken);
        if (current.Status != "Pending")
        {
            await transaction.CommitAsync(cancellationToken);
            throw new DirectoryException(409, current.UnavailableReason ?? "提案已拒绝或失效。");
        }
        var before = JsonSerializer.Serialize(proposal, Json);
        if (proposal.ActionType == "AppendWorkOrderNote")
        {
            var draft = NormalizeNote(NotePayload(proposal));
            if (NotePayloadHash(proposal, draft) != input.PayloadHash)
            {
                throw new DirectoryException(409, "Proposal payload integrity conflict.");
            }
            RequireUnexpired(proposal);
            var note = await workOrders.NoteInTransactionAsync(actor, proposal.TargetWorkOrderId!.Value,
                new(proposal.TargetWorkOrderVersion!.Value, draft.Text), proposal.Id);
            proposal.ResultEntityId = note.Id;
        }
        else
        {
            var draft = Normalize(Payload(proposal));
            if (PayloadHash(proposal, draft) != input.PayloadHash)
            {
                throw new DirectoryException(409, "Proposal payload integrity conflict.");
            }
            await ValidateAssigneeAsync(actor, proposal.PlantId, draft.AssignedToUserId, cancellationToken);
            RequireUnexpired(proposal);
            var workOrder = await workOrders.CreateInTransactionAsync(actor,
                new(proposal.AssetId, draft.Title, draft.Description, draft.Priority, proposal.AlarmId, draft.AssignedToUserId), proposal.Id);
            proposal.ResultEntityId = workOrder.Id;
        }
        proposal.Status = "Executed";
        proposal.ExecutedAt = clock.GetUtcNow();
        proposal.ApprovalCorrelationId = actor.CorrelationId;
        proposal.ExecutionKey = input.IdempotencyKey;
        Audit(actor, proposal, "Execute", before);
        await db.SaveChangesAsync(cancellationToken);
        var result = await DtoAsync(actor, proposal, run, cancellationToken);
        // Slow SQL/lock waits can cross the deadline after the initial revalidation. Roll back the entire command.
        RequireUnexpired(proposal);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }
}
