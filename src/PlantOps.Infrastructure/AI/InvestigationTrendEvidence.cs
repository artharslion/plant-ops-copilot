using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PlantOps.Application.AI;
using PlantOps.Application.Telemetry;

namespace PlantOps.Infrastructure.AI;

public sealed partial class InvestigationService
{
    private async Task<InvestigationDerivedTrend> CaptureTrendAsync(Guid plantId, Guid assetId,
        InvestigationPointSnapshot point, Guid alarmId, string kind, DateTimeOffset from, DateTimeOffset to,
        int expectedIntervalMs, CancellationToken token)
    {
        // Freeze rows, gaps and sampling metadata together; no lock spans an LLM call.
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, token);
        var fromDate = DateOnly.FromDateTime(from.UtcDateTime);
        var toDate = DateOnly.FromDateTime(to.AddTicks(-1).UtcDateTime);
        var rows = await db.MeasurementReadings.AsNoTracking().Where(x => x.PlantId == plantId && x.AssetId == assetId
            && x.PointId == point.PointId && x.EffectiveDate >= fromDate && x.EffectiveDate <= toDate
            && x.EffectiveAt >= from && x.EffectiveAt < to && x.ReceivedAt <= point.AsOf)
            .OrderBy(x => x.EffectiveAt).ThenBy(x => x.ReceivedAt).ThenBy(x => x.Id)
            .Take(InvestigationTrendPolicy.MaxSamples + 1).ToArrayAsync(token);
        var gaps = await db.IngestionGaps.AsNoTracking().Where(x => x.PointId == point.PointId
            && x.From < to && (x.To == null || x.To > from)).OrderBy(x => x.From).ThenBy(x => x.Id).Take(101).ToArrayAsync(token);
        var profiles = await db.SamplingProfiles.AsNoTracking().Where(x => x.PointId == point.PointId
            && x.EffectiveFrom < to && (x.EffectiveTo == null || x.EffectiveTo > from))
            .OrderBy(x => x.EffectiveFrom).ThenBy(x => x.Id).Take(101).ToArrayAsync(token);
        var result = InvestigationTrendPolicy.Calculate(point, alarmId, kind, from, to, clock.GetUtcNow(), rows,
            gaps.Take(100).ToArray(), profiles.Take(100).ToArray(), expectedIntervalMs,
            gaps.Length > 100 || profiles.Length > 100);
        await transaction.CommitAsync(token);
        return result;
    }

    private static string[] TrendFacts(InvestigationDerivedTrend trend)
    {
        string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
        var facts = new List<string>();
        if (trend.Change is { } change)
        {
            facts.Add($"测点 {trend.Code} 的{trend.WindowKind}有序原始观测：{change.From:O} 至 {change.To:O}，首值 {Number(change.FirstValue)} {trend.Unit}，末值 {Number(change.LastValue)} {trend.Unit}，变化量 {Number(change.Change)} {trend.Unit}，观测间平均变化率 {Number(change.RatePerMinute)} {trend.Unit}/min，样本数 {change.SampleCount}；仅代表版本已核对且段内无已知缺口的已入库观测，不证明采样间物理连续。");
        }
        foreach (var rise in trend.RiseSegments)
        {
            facts.Add($"测点 {trend.Code} 的{trend.WindowKind}非下降观测段：{rise.From:O} 至 {rise.To:O}，首值 {Number(rise.FirstValue)} {trend.Unit}，末值 {Number(rise.LastValue)} {trend.Unit}，变化量 {Number(rise.Change)} {trend.Unit}，观测间平均变化率 {Number(rise.RatePerMinute)} {trend.Unit}/min，样本数 {rise.SampleCount}，严格上升次数 {rise.IncreaseCount}；允许平台值，仅限该段，不确认根因或设备安全。");
        }
        if (facts.Count == 0)
        {
            facts.Add($"测点 {trend.Code} 的{trend.WindowKind}没有可验证的连续观测段变化；保留样本和限制，不跨坏值、缺口或未知配置计算变化。");
        }
        facts.Add($"原始变化证据范围：测点 {trend.Code}，窗口 [{trend.From:O}, {trend.To:O})，截至 {trend.AsOf:O}，实际查询 {trend.RetrievedAt:O}，关联所选告警 {trend.AlarmId}，算法 {trend.AlgorithmVersion}；查询可能晚于截止时刻，不保证截止前全部样本均已入库，首末样本不等于窗口完整覆盖。");
        facts.Add($"原始变化计算限制代码：{(trend.Reasons.Length == 0 ? "None" : string.Join(",", trend.Reasons))}；段外未知，分钟极值不证明持续升温，多点共同变化不证明共同原因。");
        // The protected observation must carry the actual rising segment even
        // when the model input budget omits the rest of this evidence entry.
        return facts.OrderBy(f => f.Contains("非下降观测段：", StringComparison.Ordinal) ? 0 : 1).ToArray();
    }
}
