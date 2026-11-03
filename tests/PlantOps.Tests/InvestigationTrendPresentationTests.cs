using System.Reflection;
using PlantOps.Application.AI;
using PlantOps.Infrastructure.AI;
using Xunit;

namespace PlantOps.Tests;

public sealed class InvestigationTrendPresentationTests
{
    [Theory]
    [InlineData("Renamed window limitation")]
    [InlineData("窗口说明已改写")]
    public void WarningPriorityComesFromMetadataAndNotAMatchingPrefix(string windowWarning)
    {
        const string ordinary = "趋势窗口不晚于：arbitrary ordinary warning";
        var priorities = new Dictionary<string, int> { [windowWarning] = 0 };
        var order = typeof(InvestigationService).GetMethod("OrderToolWarnings", BindingFlags.NonPublic | BindingFlags.Static)!;
        var warnings = (string[])order.Invoke(null, [new[] { ordinary, windowWarning }, priorities])!;
        Assert.Equal([windowWarning, ordinary], warnings);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RisingSegmentsPrecedeOverallChangeWithoutTextClassification(bool rising)
    {
        var at = DateTimeOffset.Parse("2026-10-28T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var point = Guid.NewGuid();
        var overall = new InvestigationTrendChange(Guid.NewGuid(), Guid.NewGuid(), at, at.AddMinutes(2), 80, 60, -20, -10, 3, 1);
        var segment = new InvestigationTrendChange(Guid.NewGuid(), Guid.NewGuid(), at, at.AddMinutes(1), 80, 90, 10, 10, 2, 1);
        var trend = new InvestigationDerivedTrend(InvestigationTrendPolicy.Version, point, "非下降观测段： / arbitrary", "unit",
            Guid.NewGuid(), "event", at, at.AddMinutes(3), at.AddMinutes(3), at.AddMinutes(3), [], [], [], overall,
            rising ? [segment] : [], []);
        var render = typeof(InvestigationService).GetMethod("TrendFacts", BindingFlags.NonPublic | BindingFlags.Static)!;
        string[] Facts(InvestigationDerivedTrend value) => (string[])render.Invoke(null, [value])!;
        var withoutRise = Facts(trend with
        {
            RiseSegments = []
        });
        var facts = Facts(trend);
        if (rising)
        {
            Assert.Equal(withoutRise, facts.Skip(1));
            Assert.NotEqual(withoutRise[0], facts[0]);
        }
        else
        {
            Assert.Equal(withoutRise, facts);
        }
        var empty = Facts(trend with
        {
            Change = null,
            RiseSegments = []
        });
        Assert.Equal(3, empty.Length);
    }
}
