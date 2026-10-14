using PlantOps.Application.Telemetry;
using PlantOps.Domain.Telemetry;
using Xunit;

namespace PlantOps.Tests;

public sealed class OpcUaQualityTests
{
    [Fact]
    public void GoodWithDataValueOverflowMustNotBeTrusted()
    {
        var reading = new MeasurementReading { Quality = "Good", NumericValue = 12.6, RawStatusCode = 0x480 };
        Assert.False(SamplePolicy.IsGood(reading));
    }
}
