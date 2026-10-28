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

public sealed partial class InvestigationService(PlantOpsDbContext db, IAlarmService alarms, IAssetDirectory assets,
    ITelemetryService telemetry, IWorkOrderService maintenance, IDocumentEvidenceService documents,
    IServiceProvider executionServices, IAiSettingsService settings, IAiRunConfiguration runConfiguration,
    IConfiguration configuration, IServiceScopeFactory scopes, TimeProvider clock) : IInvestigationService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

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
                run.ModelRounds, run.ToolCalls, run.InputTokens, run.OutputTokens, Language: run.Language);
        }
        JsonElement? result = run.ResultJson == null ? null : JsonDocument.Parse(run.ResultJson).RootElement.Clone();
        return new(run.Id, run.AlarmId, run.AssetId, run.Status, run.CreatedAt, run.StartedAt, run.CompletedAt,
            run.Provider, run.Model, run.Simulated, run.FailureCode, result, evidence, warnings,
            run.ModelRounds, run.ToolCalls, run.InputTokens, run.OutputTokens,
            run.RunRecordJson == null ? null : JsonSerializer.Deserialize<JsonElement>(run.RunRecordJson),
            InvestigationAssessmentPolicy.FromEvidence(evidence), run.Language);
    }

    public async Task<InvestigationRunDto> CreateAsync(Actor actor, Guid alarmId, CancellationToken cancellationToken = default, string language = "zh-CN")
    {
        if (language is not ("zh-CN" or "en"))
        {
            throw new DirectoryException(400, "Unsupported investigation language.");
        }
        var alarm = await alarms.GetAsync(actor, alarmId);
        var selection = await settings.SelectionAsync(cancellationToken);
        var run = new AiRun
        {
            ActorUserId = actor.UserId,
            CorrelationId = actor.CorrelationId,
            PlantId = alarm.PlantId,
            AssetId = alarm.AssetId,
            AlarmId = alarm.Id,
            CreatedAt = clock.GetUtcNow(),
            Provider = selection.Provider,
            Model = selection.Model,
            Language = language,
            Simulated = selection.Provider == "Fake"
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
            // A longer configured run must not be reclaimed by another Worker
            // while its original model request is still active.
            run.LeaseUntil = now.AddSeconds(Math.Clamp(configuration.GetValue("AI:RunTimeoutSeconds", 180), 1, 600) + 30);
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
        using var budget = new LlmBudget(new(
            RunTimeoutSeconds: configuration.GetValue("AI:RunTimeoutSeconds", 180),
            MaxModelRounds: configuration.GetValue("AI:MaxModelRounds", 4),
            MaxToolCalls: configuration.GetValue("AI:MaxToolCalls", 8),
            MaxInputTokens: configuration.GetValue("AI:MaxInputTokens", 16000),
            MaxOutputTokens: configuration.GetValue("AI:MaxOutputTokens", 2000)));
        budget.CheckpointAsync = async token =>
        {
            if (await CheckpointAsync(run, ledger, warnings, budget, token) != 1)
            {
                throw new OperationCanceledException(token);
            }
        };
        using var active = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Deadline);
        using var monitorStop = new CancellationTokenSource();
        var monitor = MonitorCancellationAsync(run.Id, run.LeaseId!.Value, active, monitorStop.Token);
        string status = "Succeeded";
        string? failure = null;
        string? result = null;
        try
        {
            runConfiguration.Select(run.Provider, run.Model);
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
            var capturedAt = clock.GetUtcNow();
            AlarmDto selectedAlarm;
            PointOverview point;
            PointOverview[] relatedCandidates;
            PointOverview[] relatedPoints;
            InvestigationPointSnapshot[] pointSnapshots;
            await using (var snapshotTransaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, active.Token))
            {
                selectedAlarm = await alarms.GetAsync(actor, run.AlarmId);
                var assetPoints = await telemetry.PointsAsync(actor, run.AssetId);
                point = assetPoints.Single(x => x.Id == selectedAlarm.PointId);
                // Same-asset candidates are contextual measurements, not a claim of
                // a physical dependency. Bound automatic reads independently of AI.
                relatedCandidates = assetPoints.Where(x => x.Id != point.Id && x.Enabled).OrderBy(x => x.Code, StringComparer.Ordinal).ToArray();
                relatedPoints = relatedCandidates.Take(2).ToArray();
                pointSnapshots = await CapturePointsAsync(run.PlantId, run.AssetId, new[] { point }.Concat(relatedPoints).ToArray(), capturedAt, active.Token);
                await snapshotTransaction.CommitAsync(active.Token);
            }
            var pointWindows = pointSnapshots.ToDictionary(x => x.PointId, _ => new List<InvestigationWindowAssessment>());
            InvestigationEvidence Add(string kind, string title, Guid resource, string[] facts, string? sourceHash = null, Guid? version = null, Guid? chunk = null,
                InvestigationPointSnapshot? pointStatus = null, InvestigationDerivedTrend? derivedTrend = null)
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
                var hash = sourceHash ?? Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(derivedTrend != null
                    ? JsonSerializer.Serialize(new
                    {
                        facts,
                        derivedTrend
                    }, Json) : pointStatus == null
                    ? JsonSerializer.Serialize(facts, Json) : JsonSerializer.Serialize(new
                    {
                        facts,
                        pointStatus
                    }, Json))));
                var item = new InvestigationEvidence("E" + (ledger.Count + 1).ToString(CultureInfo.InvariantCulture), kind, title,
                    facts, hash, clock.GetUtcNow(), resource, version, chunk, pointStatus, derivedTrend);
                ledger.Add(item);
                return item;
            }
            string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
            string Time(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
            Add("Alarm", "所选告警的历史快照", run.AlarmId,
                [$"触发值为 {Number(selectedAlarm.TriggerValue)} {point.CanonicalUnit}，触发时间为 {Time(selectedAlarm.StartedAt)}；这是历史告警快照。",
                 $"调查取数时告警状态为 {selectedAlarm.State}，恢复时间为 {(selectedAlarm.RecoveredAt.HasValue ? Time(selectedAlarm.RecoveredAt.Value) : "未记录")}，人工确认状态为 {(selectedAlarm.AcknowledgedAt.HasValue ? "已确认" : "未确认")}；确认不代表恢复，恢复不代表根因已消除。",
                 $"告警规则历史快照：{selectedAlarm.ThresholdSnapshot}；仅解释本事件，不作为现场操作参数。"]);
            if (!selectedAlarm.CurrentObservationTrusted)
            {
                warnings.Add("当前观测质量、陈旧或采集配置不可验证，不得用历史触发值判断当前状态或恢复。");
            }
            if (!selectedAlarm.TriggerReadingAvailable)
            {
                warnings.Add("原始触发样本已不可查询，告警证据来自保留快照。");
            }
            var triggerDate = DateOnly.FromDateTime(selectedAlarm.StartedAt.UtcDateTime);
            var trigger = await db.MeasurementReadings.AsNoTracking().SingleOrDefaultAsync(x => x.EffectiveDate == triggerDate &&
                x.Id == selectedAlarm.TriggerReadingId && x.PlantId == run.PlantId && x.AssetId == run.AssetId
                && x.PointId == selectedAlarm.PointId && x.EffectiveAt == selectedAlarm.StartedAt, active.Token);
            if (trigger != null)
            {
                var sourceName = await db.DataSources.AsNoTracking().Where(x => x.Id == trigger.DataSourceId && x.PlantId == run.PlantId)
                    .Select(x => x.Name).SingleOrDefaultAsync(active.Token);
                Add("TriggerReading", "告警原始触发样本与来源", trigger.Id,
                    [$"触发样本标识为 {trigger.Id}，来源标识为 {trigger.DataSourceId}，映射标识为 {trigger.SourceMappingId}。",
                     $"原始触发样本值为 {Number(trigger.NumericValue!.Value)} {trigger.CanonicalUnit}，有效时间为 {Time(trigger.EffectiveAt)}，质量为 {trigger.Quality}，通信来源为 {trigger.SourceKind}。",
                     $"来源登记名称：{Excerpt(sourceName ?? "不可查询", 100)}；登记名称不证明现场真实性。",
                     $"协议源时间为 {(trigger.SourceTimestamp.HasValue ? Time(trigger.SourceTimestamp.Value) : "未提供")}，原始状态码为 {trigger.RawStatusCode?.ToString(CultureInfo.InvariantCulture) ?? "未提供"}。"]);
                warnings.Add("历史触发观测来源：" + trigger.SourceKind + "；来源登记名称：" + Excerpt(sourceName ?? "不可查询", 100) + "。");
            }
            else if (point.SourceKind != null)
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
                        foreach (var selectedPoint in new[] { point }.Concat(relatedPoints))
                        {
                            var snapshot = pointSnapshots.Single(x => x.PointId == selectedPoint.Id);
                            var eventEnd = selectedAlarm.StartedAt.AddMinutes(1) < capturedAt ? selectedAlarm.StartedAt.AddMinutes(1) : capturedAt;
                            foreach (var window in new[]
                            {
                                (Kind: "EventDerivedTrend", Label: "触发事件原始窗口", From: selectedAlarm.StartedAt.AddMinutes(-2), To: eventEnd),
                                (Kind: "CurrentDerivedTrend", Label: "当前原始窗口", From: capturedAt.AddMinutes(-5), To: capturedAt)
                            })
                            {
                                var cached = ledger.SingleOrDefault(x => x.Kind == window.Kind && x.ResourceId == selectedPoint.Id);
                                if (cached != null)
                                {
                                    supplied.Add(cached);
                                    continue;
                                }
                                var derived = await CaptureTrendAsync(run.PlantId, run.AssetId, snapshot, run.AlarmId,
                                    window.Label, window.From, window.To, selectedPoint.ExpectedIntervalMs, token);
                                derived = derived with
                                {
                                    IsAlarmPoint = selectedPoint.Id == point.Id
                                };
                                supplied.Add(Add(window.Kind, "原始观测变化 · " + selectedPoint.Code + " · " + window.Label,
                                    selectedPoint.Id, TrendFacts(derived), derivedTrend: derived));
                            }
                        }
                        foreach (var window in InvestigationWindows.Create(selectedAlarm.StartedAt, selectedAlarm.RecoveredAt, capturedAt))
                        {
                            var trend = await telemetry.TrendAsync(actor, run.AssetId, point.Id, window.From, window.To, "1m");
                            pointWindows[point.Id].Add(InvestigationAssessmentPolicy.Window(window.Kind, trend));
                            var facts = new List<string> { $"{window.Label}：点位 {point.Code}，窗口 [{Time(trend.From)}, {Time(trend.To)})，单位 {trend.Unit}；仅含已完成分钟，不能由当前窗口替代事件过程。" };
                            if (window.Kind != "Trend" && window.To <= (window.Kind == "EventTrend" ? selectedAlarm.StartedAt : selectedAlarm.RecoveredAt))
                            {
                                facts.Add("事件所在分钟尚未结束，未列入完成分钟聚合；事件过程请核对原始样本，不能把此窗口当作完整事件。");
                            }
                            var anchorAt = window.Kind == "Trend" ? window.To.AddTicks(-1)
                                : window.Kind == "EventTrend" ? selectedAlarm.StartedAt : selectedAlarm.RecoveredAt!.Value;
                            foreach (var bucket in trend.Buckets.Where(x => x.Min.HasValue && x.Max.HasValue)
                                .OrderBy(x => x.Start <= anchorAt && x.Start.AddMinutes(1) > anchorAt ? 0 : 1)
                                .ThenBy(x => window.Kind == "Trend" ? -x.Start.UtcTicks : x.Start.UtcTicks))
                            {
                                facts.Add($"{window.Label}：点位 {point.Code}，已结束分钟 {Time(bucket.Start)} 至 {Time(bucket.Start.AddMinutes(1))} 的最小值为 {Number(bucket.Min!.Value)} {trend.Unit}，最大值为 {Number(bucket.Max!.Value)} {trend.Unit}，可信样本数为 {bucket.GoodCount}，采样模式为 {bucket.SamplingMode}；仅代表已入库观测。");
                            }
                            foreach (var warning in InvestigationWindows.Quality(trend, window.Label, point.Code, selectedAlarm.StartedAt, selectedAlarm.RecoveredAt, capturedAt))
                            {
                                warnings.Add(warning);
                            }
                            if (!trend.Buckets.Any(x => x.Min.HasValue))
                            {
                                facts.Add($"{window.Label}无可用分钟聚合；可能未聚合、已清理或未入库，不能据此断言设备或通信故障。");
                            }
                            supplied.Add(Add(window.Kind, window.Label, point.Id, facts.ToArray()));
                        }
                        // A separate bounded raw window preserves the sub-minute event sequence and provenance.
                        // Retained aggregates and the alarm snapshot still stand if raw partitions have expired.
                        foreach (var anchor in new[] { (Kind: "EventReadings", At: selectedAlarm.StartedAt), (Kind: "RecoveryReadings", At: selectedAlarm.RecoveredAt ?? selectedAlarm.StartedAt) }.DistinctBy(x => x.At))
                        {
                            var end = anchor.At.AddMinutes(1) < capturedAt ? anchor.At.AddMinutes(1) : capturedAt;
                            var raw = await telemetry.TrendAsync(actor, run.AssetId, point.Id, anchor.At.AddMinutes(-1), end, "raw");
                            var selected = raw.Samples.Where(x => x.EffectiveAt >= anchor.At).Take(1)
                                .Concat(raw.Samples.Where(x => x.Value.HasValue).OrderByDescending(x => x.Value).Take(1))
                                .Concat(raw.Samples.Take(1)).Concat(raw.Samples.TakeLast(1)).DistinctBy(x => x.Id);
                            supplied.Add(Add(anchor.Kind, "事件原始观测（有界节选）", point.Id,
                                selected.Select(x => $"点位 {point.Code}，样本 {x.Id}，有效时间 {Time(x.EffectiveAt)}，值 {(x.Value.HasValue ? Number(x.Value.Value) : "无值")} {point.CanonicalUnit}，质量 {x.Quality}，时间异常 {x.TimeAnomaly}，前置缺口 {x.GapBefore}，来源 {x.DataSourceId}，映射 {x.SourceMappingId}，映射版本 {x.MappingVersion}，协议 {x.SourceKind}；协议源时间不等于物理采样时间。")
                                    .DefaultIfEmpty("事件原始观测不可查询；使用历史告警快照和可用分钟聚合，不补造事件样本。").ToArray()));
                        }
                        if (relatedPoints.Length > 0 && !ledger.Any(x => x.Kind == "RelatedComparison"))
                        {
                            var comparison = new List<string>();
                            foreach (var related in relatedPoints)
                            {
                                var relatedFacts = new List<string>
                                {
                                    $"关联点位 {related.Code}；物理量 {related.Quantity}；单位 {related.CanonicalUnit}；来源 {related.SourceKind ?? "未登记"}；映射 {related.MappingId?.ToString() ?? "未登记"}；映射版本 {related.MappingVersion?.ToString(CultureInfo.InvariantCulture) ?? "未登记"}；同设备对照候选不等于已确认物理关联。"
                                };
                                foreach (var window in InvestigationWindows.Create(selectedAlarm.StartedAt, selectedAlarm.RecoveredAt, capturedAt))
                                {
                                    await Authorize(token);
                                    var relatedTrend = await telemetry.TrendAsync(actor, run.AssetId, related.Id, window.From, window.To, "1m");
                                    pointWindows[related.Id].Add(InvestigationAssessmentPolicy.Window(window.Kind, relatedTrend));
                                    var available = relatedTrend.Buckets.Where(x => x.Min.HasValue && x.Max.HasValue).ToArray();
                                    var prefix = $"关联点位 {related.Code} 的{window.Label} [{Time(relatedTrend.From)}, {Time(relatedTrend.To)})";
                                    var summary = available.Length == 0
                                        ? prefix + "无可用完成分钟聚合；未聚合、已清理或未入库待核实。"
                                        : prefix + $"：可用分钟 {available.Length}，最小值 {Number(available.Min(x => x.Min!.Value))} {relatedTrend.Unit}，最大值 {Number(available.Max(x => x.Max!.Value))} {relatedTrend.Unit}，可信样本数 {available.Sum(x => x.GoodCount)}。";
                                    if (window.Kind != "Trend" && window.To <= (window.Kind == "EventTrend" ? selectedAlarm.StartedAt : selectedAlarm.RecoveredAt))
                                    {
                                        summary += "锚点所在分钟尚未结束，以上聚合不含锚点所在分钟。";
                                    }
                                    comparison.Add(summary);
                                    relatedFacts.Add(summary);
                                    foreach (var bucket in relatedTrend.Buckets.OrderBy(x => x.Start))
                                    {
                                        relatedFacts.Add($"关联点位 {related.Code} 的{window.Label}；分钟 {Time(bucket.Start)}；最小值 {(bucket.Min.HasValue ? Number(bucket.Min.Value) : "无值")}，最大值 {(bucket.Max.HasValue ? Number(bucket.Max.Value) : "无值")} {relatedTrend.Unit}；Good {bucket.GoodCount}，Bad {bucket.BadCount}，Uncertain {bucket.UncertainCount}，时间异常 {bucket.AnomalyCount}，缺口 {bucket.HasGap}，采样模式 {bucket.SamplingMode}。");
                                    }
                                    foreach (var warning in InvestigationWindows.Quality(relatedTrend, window.Label, related.Code, selectedAlarm.StartedAt, selectedAlarm.RecoveredAt, capturedAt))
                                    {
                                        warnings.Add(warning);
                                    }
                                }
                                supplied.Add(Add("RelatedTrend", "关联点位同期趋势 · " + related.Code, related.Id, relatedFacts.ToArray()));
                            }
                            supplied.Add(Add("RelatedComparison", "自动关联趋势对照摘要", run.AlarmId,
                                [string.Join("\n", comparison) + "\n以上采用与告警点位相同的 UTC 窗口，仅含完成分钟聚合；分钟极值不能证明亚分钟同步、现场工况或因果关系，空窗口不等于测点不存在。"]));
                        }
                        else
                        {
                            supplied.AddRange(ledger.Where(x => x.Kind is "RelatedTrend" or "RelatedComparison"));
                        }
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
                            supplied.Add(Add("Document", Excerpt(doc.Title, 100) + $" · 版本 {doc.VersionNumber} · {doc.Section} · 行 {doc.StartLine}–{doc.EndLine}", doc.DocumentId,
                                [doc.Text], doc.Sha256, doc.VersionId, doc.ChunkId));
                        }
                        foreach (var warning in docs.Warnings)
                        {
                            warnings.Add(warning);
                        }
                        if (docs.Items.Count == 0)
                        {
                            warnings.Add("未检索到匹配的当前已发布授权资料，不能据此猜测规程。");
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
                    query = Excerpt(selectedAlarm.AssetCode, 64) + " " + Excerpt(point.Code, 48) + " " + Excerpt(point.Quantity, 32) + " 排查 记录 完成",
                    limit = 6
                }) : JsonSerializer.SerializeToElement(new
                {
                });
                await ReadTool(tool, args, timeout.Token);
            }
            foreach (var snapshot in pointSnapshots)
            {
                var frozen = snapshot with
                {
                    Windows = pointWindows[snapshot.PointId].ToArray()
                };
                Add("PointStatus", "测点证据状态 · " + frozen.Code, frozen.PointId, PointFacts(frozen), pointStatus: frozen);
            }
            var assessment = InvestigationAssessmentPolicy.FromEvidence(ledger)!;
            Add("Assessment", "服务端双维度判断（取证时快照）", run.AlarmId, [InvestigationAssessmentPolicy.Summary(assessment)]);
            warnings.Add("双维度判断仅适用于取证时刻的已检查点位和启用阈值；历史事件与完成分钟窗口另列，不能用历史告警推断当前异常，也不确认持续超限、根因或安全。");
            warnings.Add(relatedPoints.Length == 0
                ? "调查取数范围：已自动查询同设备授权点位目录，未找到其他启用的对照候选；不能据此断言现场不存在关联测点。"
                : "调查取数范围：已自动读取告警点位及同设备对照候选 " + string.Join("、", relatedPoints.Select(x => x.Code))
                    + " 的事件、恢复及当前完成分钟趋势；具体窗口和缺项见证据。候选按点位编号排序最多两个，不能据此确认物理关联；其余候选、跨设备趋势及现场工况未自动读取。"
                    + (relatedCandidates.Length > relatedPoints.Length ? "同设备候选超过自动读取上限，目录并非完整现场关联清单。" : ""));
            if (warnings.Count > 0)
            {
                Add("DataQuality", "服务端数据质量与范围提示", run.AlarmId, warnings.Order().ToArray());
            }
            await CheckpointAsync(run, ledger, warnings, budget, active.Token);
            var output = await executionServices.GetRequiredService<IInvestigationEngine>()
                .ExecuteAsync(ledger, ReadTool, Authorize, budget, active.Token, run.Language);
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
                .SetProperty(x => x.PromptVersion, budget.PromptVersion)
                .SetProperty(x => x.RunRecordJson, JsonSerializer.Serialize(new
                {
                    version = "investigation-run-record-v1",
                    run.LeaseId,
                    leaseAttempt = run.Attempts,
                    previousAttemptUnknown = run.Attempts > 1 && run.RunRecordJson == null,
                    previousAttempt = run.RunRecordJson == null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(run.RunRecordJson),
                    promptVersion = budget.PromptVersion,
                    language = run.Language,
                    limits = budget.Limits,
                    requests = budget.Requests,
                    evidenceSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(ledger, Json)))),
                    usage = new
                    {
                        scope = "lease-attempt",
                        source = run.Simulated ? "simulated" : budget.ReportedUsageAttempts == 0 ? "unknown" : "provider-reported",
                        complete = budget.Attempts > 0 && budget.UnknownUsageAttempts == 0,
                        actualInputTokens = budget.Attempts > 0 && budget.UnknownUsageAttempts == 0 ? (long?)budget.ActualInputTokens : null,
                        actualOutputTokens = budget.Attempts > 0 && budget.UnknownUsageAttempts == 0 ? (long?)budget.ActualOutputTokens : null,
                        reportedInputTokens = budget.ActualInputTokens,
                        reportedOutputTokens = budget.ActualOutputTokens,
                        reportedAttempts = budget.ReportedUsageAttempts,
                        unknownAttempts = budget.UnknownUsageAttempts,
                        unknownInputReservation = budget.UnknownInputReservation,
                        unknownOutputReservation = budget.UnknownOutputReservation,
                        budgetInputTokens = budget.ReservedInputTokens,
                        budgetOutputTokens = budget.ReservedOutputTokens,
                        reservedAttempts = budget.Attempts
                    }
                }, Json))
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
