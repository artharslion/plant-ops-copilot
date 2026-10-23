using PlantOps.Application.Telemetry;
using PlantOps.Infrastructure.AI;
using Xunit;

namespace PlantOps.Tests;

public sealed class InvestigationWindowsTests
{
    [Fact]
    public void DelayedAndLongRunningEventsUseIndependentBoundedUtcWindows()
    {
        var trigger = DateTimeOffset.Parse("2026-10-23T03:52:35Z");
        var recovery = trigger.AddDays(20);
        var windows = InvestigationWindows.Create(trigger, recovery, recovery.AddHours(2));
        Assert.Equal(3, windows.Count);
        Assert.All(windows, x =>
        {
            Assert.Equal(TimeSpan.FromMinutes(5), x.To - x.From);
            Assert.Equal(0, x.From.UtcTicks % TimeSpan.TicksPerMinute);
            Assert.Equal(0, x.To.UtcTicks % TimeSpan.TicksPerMinute);
        });
        Assert.True(windows[0].To < windows[1].From);
        Assert.True(windows[1].To < windows[2].From);
    }

    [Fact]
    public void ActiveMinuteIsNeverPresentedAsComplete()
    {
        var trigger = DateTimeOffset.Parse("2026-10-23T03:52:35Z");
        var windows = InvestigationWindows.Create(trigger, null, trigger.AddSeconds(1));
        Assert.Equal(DateTimeOffset.Parse("2026-10-23T03:52:00Z"), windows[0].To);
        Assert.True(windows[0].To <= trigger);
        var recovered = InvestigationWindows.Create(trigger, trigger.AddSeconds(10), trigger.AddHours(2));
        Assert.Equal(2, recovered.Count);
        Assert.Contains("触发与恢复", recovered[0].Label);
    }

    [Fact]
    public void QualityTypesRemainLocatedAndDistinguishEventOverlapFromBoundary()
    {
        var start = DateTimeOffset.Parse("2026-10-23T03:52:00Z");
        var trend = new MeasurementTrend(Guid.NewGuid(), "mm/s", start, start.AddMinutes(3), "1m", start.AddHours(2), [],
            [new(start, 4, 12.6, 5, 50, 1, 0, 0, false, 1),
             new(start.AddMinutes(1), 4, 4, 4, 50, 0, 0, 1, true, 1),
             new(start.AddMinutes(2), null, null, null, 0, 0, 0, 0, true, 0)], false, true);
        var warnings = InvestigationWindows.Quality(trend, "事件", "VIBRATION_RMS", start.AddSeconds(30), start.AddSeconds(50), trend.CheckedAt).ToArray();
        Assert.Contains(warnings, x => x.Contains("低质量") && x.Contains("与事件区间重叠"));
        Assert.Contains(warnings, x => x.Contains("时间异常") && x.Contains("不与事件区间重叠"));
        Assert.Contains(warnings, x => x.Contains("窗口结束边界"));
        Assert.All(warnings, x => Assert.Contains("VIBRATION_RMS", x));
    }
}
