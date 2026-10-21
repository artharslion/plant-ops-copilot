using PlantOps.OpcUa.Spike;
using Xunit;

namespace PlantOps.Tests;

public sealed class MinimalScenarioTests
{
    [Fact]
    public void TemperatureStagesAndRecoveryHaveFixedBoundaries()
    {
        foreach (var (second, expected) in new[] { (0, 60), (11, 60), (12, 75), (23, 75), (24, 90), (39, 90), (47, 90), (48, 60), (120, 60) })
        {
            Assert.Equal(expected, MinimalScenario.ValueAt(0, second));
            Assert.Equal(62, MinimalScenario.ValueAt(1, second));
        }
        Assert.Equal(40, MinimalScenario.StopAtSecond);
        Assert.Equal(48, MinimalScenario.RestartAtSecond);
    }

    [Fact]
    public void VibrationChangesIndependentlyAndReplayIsDeterministic()
    {
        foreach (var (second, expected) in new[] { (0, 4.0), (7, 4.0), (8, 12.6), (19, 12.6), (20, 7.0), (31, 7.0), (32, 4.0), (60, 4.0) })
        {
            Assert.Equal(expected, MinimalScenario.ValueAt(2, second));
        }
        Assert.Equal(MinimalScenario.ValueAt(0, 7), MinimalScenario.ValueAt(0, 8));
        Assert.NotEqual(MinimalScenario.ValueAt(2, 7), MinimalScenario.ValueAt(2, 8));
        Assert.Equal(3, MinimalScenario.Points.Select(point => point.Identifier).Distinct().Count());
        Assert.Equal(new[] { "degC", "degC", "mm/s" }, MinimalScenario.Points.Select(point => point.Unit));
    }
}
