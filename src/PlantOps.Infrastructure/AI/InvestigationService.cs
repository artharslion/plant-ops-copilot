using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PlantOps.Application.AI;
using PlantOps.Application.Alarms;
using PlantOps.Application.Assets;
using PlantOps.Application.Knowledge;
using PlantOps.Application.Maintenance;
using PlantOps.Application.Telemetry;
using PlantOps.Domain.AI;
using PlantOps.Infrastructure.Persistence;

namespace PlantOps.Infrastructure.AI;

public sealed class InvestigationService(PlantOpsDbContext db, IAlarmService alarms, IAssetDirectory assets,
    ITelemetryService telemetry, IWorkOrderService maintenance, IDocumentEvidenceService documents,
    IInvestigationEngine engine, IConfiguration configuration, IServiceScopeFactory scopes, TimeProvider clock) : IInvestigationService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private string Provider => configuration["AI:Provider"] ?? "Fake";

    private string Model => Provider == "Fake" ? "fake" : configuration["AI:Model"] ?? "gpt-6.1-sol";

    private static InvestigationEvidence[] Ledger(AiRun run) => JsonSerializer.Deserialize<InvestigationEvidence[]>(run.EvidenceJson, Json) ?? [];

    private async Task AuthorizeAsync(AiRun run, IReadOnlyList<InvestigationEvidence> evidence, CancellationToken cancellationToken)
    {
        var enabled = await db.Users.AsNoTracking().AnyAsync(x => x.Id == run.ActorUserId && x.IsEnabled, cancellationToken);
        if (!enabled)
        {
            throw new DirectoryException(401, "Session unavailable.");
        }
        var roles = await (from grant in db.UserRoles
                           join role in db.Roles on grant.RoleId equals role.Id
                           where grant.UserId == run.ActorUserId
                           select role.Name).ToListAsync(cancellationToken);
        if (!roles.Any(x => x is "Admin" or "MaintenanceEngineer" or "Operator" or "Viewer"))
        {
            throw new DirectoryException(403, "Role does not allow this action.");
        }
        if (!await db.UserPlantAccess.AsNoTracking().AnyAsync(x => x.UserId == run.ActorUserId && x.PlantId == run.PlantId, cancellationToken)
            || !await db.Assets.AsNoTracking().AnyAsync(x => x.Id == run.AssetId && x.PlantId == run.PlantId && !x.IsArchived, cancellationToken)
            || !await db.Plants.AsNoTracking().AnyAsync(x => x.Id == run.PlantId && !x.IsArchived, cancellationToken)
            || !await db.Alarms.AsNoTracking().AnyAsync(x => x.Id == run.AlarmId && x.AssetId == run.AssetId && x.PlantId == run.PlantId, cancellationToken))
        {
            throw new DirectoryException(404, "Investigation not available.");
        }
        foreach (var item in evidence.Where(x => x.Kind == "Document"))
        {
            if (item.VersionId == null || item.ChunkId == null)
            {
                throw new DirectoryException(409, "Evidence changed.");
            }
            var current = await documents.ExpandAsync(new(run.ActorUserId, run.CorrelationId), run.AssetId,
                item.ResourceId, item.VersionId.Value, item.ChunkId.Value, cancellationToken);
            if (current.Sha256 != item.Sha256)
            {
                throw new DirectoryException(409, "Evidence changed.");
            }
        }
    }

    private async Task<AiRun> OwnedAsync(Actor actor, Guid id, CancellationToken cancellationToken)
    {
        var run = await db.AiRuns.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.ActorUserId == actor.UserId, cancellationToken)
            ?? throw new DirectoryException(404, "Investigation not found.");
        await AuthorizeAsync(run, [], cancellationToken);
        return run;
    }

    private async Task<InvestigationRunDto> DtoAsync(AiRun run, CancellationToken cancellationToken)
    {
        var evidence = Ledger(run);
        var warnings = JsonSerializer.Deserialize<string[]>(run.WarningsJson, Json) ?? [];
        try
        {
            await AuthorizeAsync(run, evidence, cancellationToken);
        }
        catch (DirectoryException exception) when (exception.Status is 404 or 409 && evidence.Any(x => x.Kind == "Document"))
        {
            await AuthorizeAsync(run, [], cancellationToken);
            return new(run.Id, run.AlarmId, run.AssetId, "Invalidated", run.CreatedAt, run.StartedAt, run.CompletedAt,
                run.Provider, run.Model, run.Simulated, "EvidenceChanged", null, [], ["资料版本或读取权限已变化，旧调查内容已隐藏，请重新调查。"],
                run.ModelRounds, run.ToolCalls, run.InputTokens, run.OutputTokens);
        }
        JsonElement? result = run.ResultJson == null ? null : JsonDocument.Parse(run.ResultJson).RootElement.Clone();
        return new(run.Id, run.AlarmId, run.AssetId, run.Status, run.CreatedAt, run.StartedAt, run.CompletedAt,
            run.Provider, run.Model, run.Simulated, run.FailureCode, result, evidence, warnings,
            run.ModelRounds, run.ToolCalls, run.InputTokens, run.OutputTokens);
    }

    public async Task<InvestigationRunDto> CreateAsync(Actor actor, Guid alarmId, CancellationToken cancellationToken = default)
    {
        var alarm = await alarms.GetAsync(actor, alarmId);
        var run = new AiRun
        {
            ActorUserId = actor.UserId,
            CorrelationId = actor.CorrelationId,
            PlantId = alarm.PlantId,
            AssetId = alarm.AssetId,
            AlarmId = alarm.Id,
            CreatedAt = clock.GetUtcNow(),
            Provider = Provider,
            Model = Model,
            Simulated = Provider == "Fake"
        };
        await AuthorizeAsync(run, [], cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"ai-create:" + actor.UserId}, 0))", cancellationToken);
        await AuthorizeAsync(run, [], cancellationToken);
        var active = db.AiRuns.AsNoTracking().Where(x => x.ActorUserId == actor.UserId && (x.Status == "Pending" || x.Status == "Running"));
        var existing = await active.SingleOrDefaultAsync(x => x.AlarmId == alarmId, cancellationToken);
        if (existing != null)
        {
            await transaction.CommitAsync(cancellationToken);
            return await DtoAsync(existing, cancellationToken);
        }
        if (await active.CountAsync(cancellationToken) >= 2)
        {
            throw new DirectoryException(429, "At most two investigations can be active per user.");
        }
        db.AiRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await DtoAsync(run, cancellationToken);
    }

    public async Task<IReadOnlyList<InvestigationRunDto>> ListAsync(Actor actor, Guid alarmId, CancellationToken cancellationToken = default)
    {
        var alarm = await alarms.GetAsync(actor, alarmId);
        await AuthorizeAsync(new AiRun { ActorUserId = actor.UserId, AlarmId = alarmId, AssetId = alarm.AssetId, PlantId = alarm.PlantId }, [], cancellationToken);
        var rows = await db.AiRuns.AsNoTracking().Where(x => x.ActorUserId == actor.UserId && x.AlarmId == alarmId)
            .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Take(10).ToListAsync(cancellationToken);
        var results = new List<InvestigationRunDto>();
        foreach (var run in rows)
        {
            results.Add(await DtoAsync(run, cancellationToken));
        }
        return results;
    }

    public async Task<InvestigationRunDto> GetAsync(Actor actor, Guid id, CancellationToken cancellationToken = default) =>
        await DtoAsync(await OwnedAsync(actor, id, cancellationToken), cancellationToken);

    public async Task<InvestigationRunDto> CancelAsync(Actor actor, Guid id, CancellationToken cancellationToken = default)
    {
        await OwnedAsync(actor, id, cancellationToken);
        await db.AiRuns.Where(x => x.Id == id && x.ActorUserId == actor.UserId && (x.Status == "Pending" || x.Status == "Running"))
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.Status, "Cancelled").SetProperty(x => x.CompletedAt, clock.GetUtcNow())
                .SetProperty(x => x.ResultJson, (string?)null), cancellationToken);
        return await GetAsync(actor, id, cancellationToken);
    }

    public async Task<InvestigationEvidence> ExpandAsync(Actor actor, Guid id, string evidenceId, CancellationToken cancellationToken = default)
    {
        var run = await OwnedAsync(actor, id, cancellationToken);
        var evidence = Ledger(run);
        await AuthorizeAsync(run, evidence, cancellationToken);
        var item = evidence.SingleOrDefault(x => x.Id == evidenceId) ?? throw new DirectoryException(404, "Evidence not found.");
        if (item.Kind == "Document")
        {
            var source = await documents.ExpandAsync(actor, run.AssetId, item.ResourceId, item.VersionId!.Value, item.ChunkId!.Value, cancellationToken);
            return item with
            {
                Title = source.Title + $" · 版本 {source.VersionNumber} · 行 {source.StartLine}–{source.EndLine}",
                Facts = [source.Text]
            };
        }
        return item;
    }

    private async Task<AiRun?> ClaimAsync(CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var now = clock.GetUtcNow();
        var run = await db.AiRuns.FromSqlInterpolated($"SELECT * FROM \"AiRuns\" WHERE \"Status\" = 'Pending' OR (\"Status\" = 'Running' AND \"LeaseUntil\" < {now}) ORDER BY \"CreatedAt\", \"Id\" LIMIT 1 FOR UPDATE SKIP LOCKED")
            .SingleOrDefaultAsync(cancellationToken);
        if (run == null)
        {
            return null;
        }
        if (run.Attempts >= 2)
        {
            run.Status = "Failed";
            run.FailureCode = "LeaseExpired";
            run.CompletedAt = now;
        }
        else
        {
            run.Status = "Running";
            run.StartedAt = now;
            run.LeaseId = Guid.NewGuid();
            run.LeaseUntil = now.AddSeconds(90);
            run.Attempts++;
            run.ResultJson = null;
            run.EvidenceJson = "[]";
            run.WarningsJson = "[]";
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        db.Entry(run).State = EntityState.Detached;
        return run;
    }

    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken = default)
    {
        var run = await ClaimAsync(cancellationToken);
        if (run == null)
        {
            return false;
        }
        if (run.Status != "Running")
        {
            return true;
        }
        var ledger = new List<InvestigationEvidence>();
        var warnings = new HashSet<string>(StringComparer.Ordinal);
        using var budget = new LlmBudget();
        using var active = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Deadline);
        using var monitorStop = new CancellationTokenSource();
        var monitor = MonitorCancellationAsync(run.Id, run.LeaseId!.Value, active, monitorStop.Token);
        string status = "Succeeded";
        string? failure = null;
        string? result = null;
        try
        {
            if (run.Provider != Provider || run.Provider != "Fake" && run.Model != Model)
            {
                throw new LlmException(LlmFailure.InvalidConfiguration);
            }
            async Task Authorize(CancellationToken token)
            {
                await AuthorizeAsync(run, ledger, token);
                if (!await db.AiRuns.AsNoTracking().AnyAsync(x => x.Id == run.Id && x.Status == "Running" && x.LeaseId == run.LeaseId, token))
                {
                    throw new OperationCanceledException(token);
                }
            }
            await Authorize(active.Token);
            var actor = new Actor(run.ActorUserId, run.CorrelationId);
            var selectedAlarm = await alarms.GetAsync(actor, run.AlarmId);
            var point = (await telemetry.PointsAsync(actor, run.AssetId)).Single(x => x.Id == selectedAlarm.PointId);
            var capturedAt = clock.GetUtcNow();
            InvestigationEvidence Add(string kind, string title, Guid resource, string[] facts, string? sourceHash = null, Guid? version = null, Guid? chunk = null)
            {
                var existing = ledger.SingleOrDefault(x => x.Kind == kind && x.ResourceId == resource && x.VersionId == version && x.ChunkId == chunk);
                if (existing != null)
                {
                    return existing;
                }
                if (ledger.Count >= 40)
                {
                    throw new LlmException(LlmFailure.BudgetExceeded);
                }
                var hash = sourceHash ?? Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(facts, Json))));
                var item = new InvestigationEvidence("E" + (ledger.Count + 1).ToString(CultureInfo.InvariantCulture), kind, title,
                    facts, hash, capturedAt, resource, version, chunk);
                ledger.Add(item);
                return item;
            }
            string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
            string Time(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
            Add("Alarm", "所选告警的历史快照", run.AlarmId,
                [$"触发值为 {Number(selectedAlarm.TriggerValue)} {point.CanonicalUnit}，触发时间为 {Time(selectedAlarm.StartedAt)}；这是历史告警快照。",
                 $"调查取数时告警状态为 {selectedAlarm.State}，人工确认状态为 {(selectedAlarm.AcknowledgedAt.HasValue ? "已确认" : "未确认")}；确认不代表恢复。"]);
            if (!selectedAlarm.CurrentObservationTrusted)
            {
                warnings.Add("当前观测质量、陈旧或采集配置不可验证，不得用历史触发值判断当前状态或恢复。");
            }
            if (!selectedAlarm.TriggerReadingAvailable)
            {
                warnings.Add("原始触发样本已不可查询，告警证据来自保留快照。");
            }
            if (point.SourceKind != null)
            {
                warnings.Add("观测来源：" + point.SourceKind + "；请区分模拟与现场观测。");
            }
            async Task<InvestigationToolResult> ReadTool(string name, JsonElement arguments, CancellationToken token)
            {
                await Authorize(token);
                var supplied = new List<InvestigationEvidence>();
                switch (name)
                {
                    case "get_asset_details":
                        var asset = await assets.GetAsync(actor, "assets", run.AssetId);
                        supplied.Add(Add("Asset", "设备资料", run.AssetId, [$"设备编号为 {asset.Code}，设备名称为 {Excerpt(asset.Name, 100)}，类型为 {asset.AssetType}。"]));
                        break;
                    case "get_recent_measurements":
                        var to = new DateTimeOffset(capturedAt.UtcDateTime.AddTicks(-(capturedAt.UtcTicks % TimeSpan.TicksPerMinute)), TimeSpan.Zero);
                        var trend = await telemetry.TrendAsync(actor, run.AssetId, point.Id, to.AddHours(-1), to, "1m");
                        var facts = new List<string> { $"趋势窗口为 {Time(trend.From)} 至 {Time(trend.To)}，单位为 {trend.Unit}；仅含已完成分钟。" };
                        var valid = trend.Buckets.Where(x => x.Min.HasValue && x.Max.HasValue).ToArray();
                        if (valid.Length > 0)
                        {
                            facts.Add($"窗口内有值分钟的最小值为 {Number(valid.Min(x => x.Min!.Value))} {trend.Unit}，最大值为 {Number(valid.Max(x => x.Max!.Value))} {trend.Unit}；这不代表当前实时值。");
                        }
                        else
                        {
                            warnings.Add("最近一小时没有可用分钟测量值，不补造趋势。");
                        }
                        if (trend.LeadingGap || trend.TrailingGap || trend.Buckets.Any(x => x.HasGap || x.BadCount > 0 || x.UncertainCount > 0 || x.AnomalyCount > 0))
                        {
                            warnings.Add("趋势包含缺口、低质量或时间异常，统计不能证明连续正常运行。");
                        }
                        supplied.Add(Add("Trend", "最近一小时分钟趋势", point.Id, facts.ToArray()));
                        break;
                    case "get_recent_alarms":
                        var alarmPage = await alarms.ListAsync(actor, run.PlantId, null, run.AssetId, 100, null);
                        var recent = alarmPage.Items.Where(x => x.StartedAt >= capturedAt.AddDays(-30) && x.StartedAt <= capturedAt).ToArray();
                        supplied.Add(Add("AlarmHistory", "近期告警", run.AssetId,
                            [$"最近三十天查询到 {recent.Length} 条设备告警；每次最多检查一百条，人工确认与恢复分别记录。"]));
                        if (alarmPage.NextCursor != null)
                        {
                            warnings.Add("近期告警超过读取上限，数量仅代表已读记录。");
                        }
                        break;
                    case "get_maintenance_history":
                        var work = await maintenance.ListAsync(actor, run.AssetId, false, 20, null);
                        supplied.Add(Add("Maintenance", "维护记录摘要", run.AssetId,
                            work.Items.Count == 0 ? ["没有查询到设备维护工单；不代表现场从未维护。"]
                            : work.Items.Take(5).Select(x => $"维护记录：{Excerpt(x.Title, 80)}；状态为 {x.Status}；处理结果摘录：{Excerpt(x.Resolution ?? "未记录", 120)}。").ToArray()));
                        if (work.NextCursor != null || work.Items.Count > 5)
                        {
                            warnings.Add("维护记录仅发送最多五条摘要，其余记录请人工查阅。");
                        }
                        break;
                    case "search_documents":
                        var docs = await documents.SearchAsync(actor, run.AssetId, arguments.GetProperty("query").GetString()!, arguments.GetProperty("limit").GetInt32(), token);
                        foreach (var doc in docs.Items)
                        {
                            supplied.Add(Add("Document", Excerpt(doc.Title, 100), doc.DocumentId,
                                ["资料摘录：" + Excerpt(doc.Text, 150)], doc.Sha256, doc.VersionId, doc.ChunkId));
                        }
                        foreach (var warning in docs.Warnings)
                        {
                            warnings.Add(warning);
                        }
                        if (docs.Items.Count == 0)
                        {
                            warnings.Add("未检索到匹配的当前已发布授权资料，不能据此猜测规程。");
                        }
                        if (docs.Items.Any(x => x.Text.Length > 150))
                        {
                            warnings.Add("发送给模型的资料为节选，完整片段需展开引用并由人员核对。");
                        }
                        break;
                    default:
                        throw new LlmException(LlmFailure.InvalidRequest);
                }
                await Authorize(token);
                return new(supplied, warnings.Order().ToArray());
            }
            foreach (var tool in new[] { "get_asset_details", "get_recent_measurements", "get_recent_alarms", "get_maintenance_history", "search_documents" })
            {
                budget.RecordToolCalls(1);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(active.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var args = tool == "search_documents" ? JsonSerializer.SerializeToElement(new
                {
                    query = Excerpt(selectedAlarm.AssetCode, 64) + " vibration 振动",
                    limit = 3
                }) : JsonSerializer.SerializeToElement(new
                {
                });
                await ReadTool(tool, args, timeout.Token);
            }
            if (warnings.Count > 0)
            {
                Add("DataQuality", "服务端数据质量与范围提示", run.AlarmId, warnings.Order().ToArray());
            }
            await CheckpointAsync(run, ledger, warnings, budget, active.Token);
            var output = await engine.ExecuteAsync(ledger, ReadTool, Authorize, budget, active.Token);
            await Authorize(active.Token);
            result = output.ResultJson;
            run.Model = output.Model;
            run.Simulated = output.Simulated;
        }
        catch (DirectoryException)
        {
            status = "Failed";
            failure = "AuthorizationChanged";
        }
        catch (LlmException exception)
        {
            status = "Failed";
            failure = exception.Failure.ToString();
        }
        catch (OperationCanceledException)
        {
            status = "Failed";
            failure = active.IsCancellationRequested && !budget.Deadline.IsCancellationRequested ? "Cancelled" : "Timeout";
        }
        catch (Exception)
        {
            status = "Failed";
            failure = "InvestigationUnavailable";
        }
        finally
        {
            monitorStop.Cancel();
            await monitor;
        }
        if (cancellationToken.IsCancellationRequested)
        {
            return true; // Keep the lease for restart recovery; no partial success on shutdown.
        }
        await CheckpointAsync(run, ledger, warnings, budget, CancellationToken.None);
        await db.AiRuns.Where(x => x.Id == run.Id && x.Status == "Running" && x.LeaseId == run.LeaseId)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.Status, status).SetProperty(x => x.FailureCode, failure)
                .SetProperty(x => x.ResultJson, result).SetProperty(x => x.Model, run.Model).SetProperty(x => x.Simulated, run.Simulated)
                .SetProperty(x => x.CompletedAt, clock.GetUtcNow()), CancellationToken.None);
        return true;
    }

    private Task<int> CheckpointAsync(AiRun run, IReadOnlyList<InvestigationEvidence> ledger, IReadOnlySet<string> warnings, LlmBudget budget, CancellationToken cancellationToken) =>
        db.AiRuns.Where(x => x.Id == run.Id && x.Status == "Running" && x.LeaseId == run.LeaseId)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.EvidenceJson, JsonSerializer.Serialize(ledger, Json))
                .SetProperty(x => x.WarningsJson, JsonSerializer.Serialize(warnings.Order().ToArray(), Json))
                .SetProperty(x => x.ModelRounds, budget.ModelRounds).SetProperty(x => x.ToolCalls, budget.ToolCalls)
                .SetProperty(x => x.InputTokens, budget.ReservedInputTokens).SetProperty(x => x.OutputTokens, budget.ReservedOutputTokens), cancellationToken);

    private async Task MonitorCancellationAsync(Guid id, Guid lease, CancellationTokenSource active, CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(300, stoppingToken);
                await using var scope = scopes.CreateAsyncScope();
                var monitorDb = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
                if (!await monitorDb.AiRuns.AsNoTracking().AnyAsync(x => x.Id == id && x.Status == "Running" && x.LeaseId == lease, stoppingToken))
                {
                    active.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception) { active.Cancel(); }
    }

    private static string Excerpt(string value, int maximum)
    {
        if (value.Length <= maximum)
        {
            return value;
        }
        return value[..(char.IsHighSurrogate(value[maximum - 1]) ? maximum - 1 : maximum)] + "…";
    }
}
