using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PlantOps.Application.AI;
using PlantOps.Application.Telemetry;

namespace PlantOps.Infrastructure.AI;

public sealed partial class InvestigationService
{
    // Called in a short read snapshot. No transaction spans tool/model execution.
    private async Task<InvestigationPointSnapshot[]> CapturePointsAsync(Guid plantId, Guid assetId,
        IReadOnlyList<PointOverview> points, DateTimeOffset asOf, CancellationToken token)
    {
        var selectedIds = points.Select(x => x.Id).ToArray();
        var missing = await db.MeasurementPoints.AsNoTracking().CountAsync(x => x.PlantId == plantId
            && x.AssetId == assetId && x.Enabled && !selectedIds.Contains(x.Id), token);
        var result = new List<InvestigationPointSnapshot>();
        foreach (var point in points)
        {
            var source = point.DataSourceId.HasValue ? await db.DataSources.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == point.DataSourceId && x.PlantId == plantId, token) : null;
            var latest = point.LatestObserved;
            var date = latest == null ? DateOnly.MinValue : DateOnly.FromDateTime(latest.EffectiveAt.UtcDateTime);
            var reading = latest == null ? null : await db.MeasurementReadings.AsNoTracking().SingleOrDefaultAsync(x =>
                x.EffectiveDate == date && x.Id == latest.Id && x.PointId == point.Id && x.AssetId == assetId && x.PlantId == plantId, token);
            var health = source == null ? null : await db.DataSourceHealth.AsNoTracking().SingleOrDefaultAsync(x => x.DataSourceId == source.Id, token);
            var profile = await db.SamplingProfiles.AsNoTracking().Where(x => x.PointId == point.Id
                && x.SourceMappingId == point.MappingId && x.MappingVersion == point.MappingVersion
                && x.EffectiveFrom <= asOf && (x.EffectiveTo == null || x.EffectiveTo > asOf))
                .OrderByDescending(x => x.EffectiveFrom).FirstOrDefaultAsync(token);
            var candidates = await db.AlarmRules.AsNoTracking().Where(x => x.PlantId == plantId && x.PointId == point.Id && x.Enabled)
                .OrderBy(x => x.Id).Take(21).ToArrayAsync(token);
            var rules = candidates.Take(20).ToArray();
            // Legacy raw rows intentionally retain unknown versions. Never infer
            // an individual sample's configuration from current rule runtime state.
            var versionsConfirmed = source != null && reading?.SourceVersion == source.Version && reading?.PointVersion == point.Version;
            var snapshot = new InvestigationPointSnapshot
            {
                PointId = point.Id,
                Code = point.Code,
                Unit = point.CanonicalUnit,
                AsOf = asOf,
                RetrievedAt = clock.GetUtcNow(),
                PointVersion = point.Version,
                SourceId = source?.Id,
                SourceVersion = source?.Version,
                MappingId = point.MappingId,
                MappingVersion = point.MappingVersion,
                SourceKind = source?.Kind,
                SamplingMode = source?.Kind == "Simulation" ? "Periodic" : profile?.Mode ?? "Unknown",
                StaleAfterMs = point.StaleAfterMs,
                UninspectedPointCount = missing,
                ConfigurationCurrent = point.Enabled && source is { Enabled: true } && versionsConfirmed && point.MappingId.HasValue
                    && (source.Kind != "OpcUa" || profile != null && reading?.ReceivedAt >= profile.EffectiveFrom),
                LatestStateTrusted = latest?.IsTrusted == true,
                Observation = reading == null ? null : new InvestigationObservedReading(reading.Id, reading.EffectiveAt, reading.ReceivedAt,
                    reading.NumericValue, reading.Quality, reading.TimeAnomaly, reading.DataSourceId, reading.SourceMappingId,
                    reading.MappingVersion, reading.SourceKind, reading.TimestampOrigin, reading.SourceTimestamp, reading.ServerTimestamp,
                    reading.RawStatusCode, reading.StatusSymbol, reading.SourceVersion, reading.PointVersion),
                Health = health == null ? null : new SourceHealth(health.DataSourceId, health.SourceVersion, health.State,
                    health.UpdatedAt, health.LastKeepAliveAt, health.LastReceivedAt, health.ReconnectCount, health.FailureCode,
                    source!.Enabled && health.SourceVersion == source.Version && health.UpdatedAt <= asOf && asOf - health.UpdatedAt <= TimeSpan.FromSeconds(10)),
                Rules = rules.Select(x => new InvestigationRuleSnapshot(x.Id, x.Version, x.RaiseAbove, x.ClearBelow)).ToArray(),
                RulesTruncated = candidates.Length > rules.Length
            };
            result.Add(InvestigationAssessmentPolicy.Evaluate(snapshot));
        }
        return result.ToArray();
    }

    private static string[] PointFacts(InvestigationPointSnapshot point)
    {
        var reading = point.Observation;
        string Number(double? value) => value?.ToString("R", CultureInfo.InvariantCulture) ?? "无值";
        string[] facts =
        [
            $"测点 {point.Code} 截至 {point.AsOf:O}：当前数据{InvestigationAssessmentPolicy.TrustLabel(point.CurrentTrust)}，当前异常证据{InvestigationAssessmentPolicy.AnomalyLabel(point.CurrentAnomaly)}；值 {Number(reading?.Value)} {point.Unit}；质量 {reading?.Quality ?? "未提供"}；仅为启用阈值下的有界比较，不确认持续超限或根因。",
            $"测点 {point.Code}；样本 {reading?.Id.ToString() ?? "不可查询"}；有效时间 {reading?.EffectiveAt.ToString("O") ?? "未知"}；接收时间 {reading?.ReceivedAt.ToString("O") ?? "未知"}；时间来源 {reading?.TimestampOrigin ?? "未知"}；来源 {point.SourceId?.ToString() ?? "未知"}；当前来源版本 {point.SourceVersion?.ToString(CultureInfo.InvariantCulture) ?? "未知"}；映射 {point.MappingId?.ToString() ?? "未知"}；映射版本 {point.MappingVersion?.ToString(CultureInfo.InvariantCulture) ?? "未知"}；采样模式 {point.SamplingMode}。",
            $"测点 {point.Code} 的判定依据代码：{string.Join("、", point.Reasons)}；来源健康 {point.Health?.State ?? "未知"}，健康记录是否当前 {point.Health?.IsCurrent.ToString() ?? "未知"}。会话保活、无通知或旧可信值不能单独证明当前正常，也不能单独证明传感器故障。"
        ];
        return facts.Append($"测点 {point.Code} 的原始样本所记录来源版本 {reading?.SourceVersion?.ToString(CultureInfo.InvariantCulture) ?? "未知"}，测点版本 {reading?.PointVersion?.ToString(CultureInfo.InvariantCulture) ?? "未知"}；当前测点版本 {point.PointVersion}。未知版本不得用当前配置追认。")
            .Concat(point.Rules.Select(rule => $"测点 {point.Code} 的启用规则 {rule.Id}，版本 {rule.Version}；当前值大于 {Number(rule.RaiseAbove)} {point.Unit} 时为超阈值证据，小于 {Number(rule.ClearBelow)} {point.Unit} 时低于恢复界限；本比较不替代持续时间、回差告警状态机。"))
            .Concat(point.RulesTruncated ? ["启用规则超过二十条，本次规则范围已截断，不能据已读规则作全范围未发现异常结论。"] : Array.Empty<string>()).ToArray();
    }
}
