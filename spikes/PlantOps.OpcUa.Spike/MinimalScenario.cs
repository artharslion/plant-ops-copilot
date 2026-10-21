using System.Collections.Concurrent;
using System.Diagnostics;

namespace PlantOps.OpcUa.Spike;

// Versioned, original simulated observations. No random input (seed 0).
internal sealed class MinimalScenario
{
    public const string Version = "p5.3-minimal-v1";
    public const int StopAtSecond = 40;
    public const int RestartAtSecond = 48;
    public static readonly ScenarioPoint[] Points =
    [
        new("North.Utilities.P101.Temperature", "TEMPERATURE", "Temperature", "degC", 4408652),
        new("North.Utilities.P101.ReferenceTemperature", "REFERENCE_TEMPERATURE", "Temperature", "degC", 4408652),
        new("North.Utilities.P101.ScenarioVibrationRms", "SCENARIO_VIBRATION", "Vibration", "mm/s", 4403510)
    ];

    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly ConcurrentQueue<ScenarioObservation> generated = new();

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    public double ElapsedSeconds => clock.Elapsed.TotalSeconds;

    public ScenarioObservation[] Generated => generated.ToArray();

    public static double ValueAt(int point, int second) => point switch
    {
        0 => second < 12 ? 60 : second < 24 ? 75 : second < 48 ? 90 : 60,
        1 => 62,
        2 => second < 8 ? 4 : second < 20 ? 12.6 : second < 32 ? 7 : 4,
        _ => throw new ArgumentOutOfRangeException(nameof(point))
    };

    public ScenarioObservation Generate(int point)
    {
        var second = (int)ElapsedSeconds;
        var observation = new ScenarioObservation(Points[point].Identifier, second,
            ValueAt(point, second), StartedAt.AddSeconds(second));
        generated.Enqueue(observation);
        return observation;
    }
}

internal sealed record ScenarioPoint(string Identifier, string Code, string Quantity, string Unit, int UnitId);

internal sealed record ScenarioObservation(string Identifier, int Second, double Value, DateTimeOffset SourceTimestamp);
