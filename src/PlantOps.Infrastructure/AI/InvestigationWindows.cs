using PlantOps.Application.Telemetry;

namespace PlantOps.Infrastructure.AI;

public sealed record InvestigationWindow(string Kind, string Label, DateTimeOffset From, DateTimeOffset To);

public static class InvestigationWindows
{
    private static DateTimeOffset Minute(DateTimeOffset at) => new(at.UtcTicks - at.UtcTicks % TimeSpan.TicksPerMinute, TimeSpan.Zero);

    public static IReadOnlyList<InvestigationWindow> Create(DateTimeOffset triggered, DateTimeOffset? recovered, DateTimeOffset captured)
    {
        var complete = Minute(captured);
        var result = new List<InvestigationWindow>();
        void Event(string kind, string label, DateTimeOffset at)
        {
            var from = Minute(at).AddMinutes(-2);
            var to = Minute(at).AddMinutes(3);
            if (to > complete)
            {
                to = complete;
            }
            if (from < to)
            {
                result.Add(new(kind, label, from, to));
            }
        }
        Event("EventTrend", "触发事件窗口", triggered);
        // Separate anchors avoid scanning a long-running alarm's entire lifetime.
        if (recovered.HasValue)
        {
            if (result.Count > 0 && Minute(recovered.Value) == Minute(triggered))
            {
                result[0] = result[0] with
                {
                    Label = "触发与恢复事件窗口"
                };
            }
            else
            {
                Event("RecoveryTrend", "恢复事件窗口", recovered.Value);
            }
        }
        result.Add(new("Trend", "调查当前窗口", complete.AddMinutes(-5), complete));
        return result;
    }

    public static IEnumerable<string> Quality(MeasurementTrend trend, string label, string point,
        DateTimeOffset triggered, DateTimeOffset? recovered, DateTimeOffset captured)
    {
        string Range(DateTimeOffset from, DateTimeOffset to, string type)
        {
            var overlap = from < (recovered ?? captured) && to > triggered;
            return $"{label}；点位 {point}；[{from:O}, {to:O})；类型：{type}；{(overlap ? "与事件区间重叠（分钟级定位）" : "不与事件区间重叠")}。质量缺失不能单独支持传感器或通信故障判断。";
        }
        foreach (var (type, test) in new (string, Func<TrendBucket, bool>)[]
        {
            ("缺口标记", x => x.HasGap && x.GoodCount + x.BadCount + x.UncertainCount + x.AnomalyCount > 0),
            ("聚合不可用或无观测（未聚合/清理/未入库待核实）", x => x.HasGap && x.GoodCount + x.BadCount + x.UncertainCount + x.AnomalyCount == 0),
            ("低质量（Bad/Uncertain）", x => x.BadCount > 0 || x.UncertainCount > 0),
            ("时间异常", x => x.AnomalyCount > 0)
        })
        {
            DateTimeOffset? start = null;
            DateTimeOffset end = default;
            foreach (var bucket in trend.Buckets)
            {
                if (test(bucket))
                {
                    start ??= bucket.Start;
                    end = bucket.Start.AddMinutes(1);
                }
                else if (start.HasValue)
                {
                    yield return Range(start.Value, end, type);
                    start = null;
                }
            }
            if (start.HasValue)
            {
                yield return Range(start.Value, end, type);
            }
        }
        if (trend.LeadingGap)
        {
            yield return Range(trend.From, trend.From.AddMinutes(1), "窗口起始边界分钟含缺口/不可用标记（具体边界覆盖待核实）");
        }
        if (trend.TrailingGap)
        {
            yield return Range(trend.To.AddMinutes(-1), trend.To, "窗口结束边界分钟含缺口/不可用标记（具体边界覆盖待核实）");
        }
    }
}
