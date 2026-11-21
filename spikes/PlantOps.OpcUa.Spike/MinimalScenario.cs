using System.Collections.Concurrent;
using System.Diagnostics;

namespace PlantOps.OpcUa.Spike;

internal interface IProtocolScenario
{
    ScenarioPoint[] Definitions
    {
        get;
    }

    ScenarioObservation Generate(int point);
}

// Versioned, original simulated observations. No random input (seed 0).
internal sealed class MinimalScenario(bool mixedQuality = false, bool reliability = false) : IProtocolScenario
{
    public const string Version = "p5.3-minimal-v1";
    public const int StopAtSecond = 40;
    public const int RestartAtSecond = 48;
    public const int ReliabilityDurationSeconds = 1800;
    public const int ReliabilityBadFromSecond = 70;
    public const int ReliabilityBadToSecond = 84;
    public static readonly int[] ReliabilityStopSeconds = [40, 100, 190];

    internal static bool LiveHeating => Environment.GetEnvironmentVariable("PLANTOPS_P61_LIVE_HEATING") == "1";

    public static readonly ScenarioPoint[] Points =
    [
        new("North.Utilities.P101.Temperature", "TEMPERATURE", "Temperature", "degC", 4408652),
        new("North.Utilities.P101.ReferenceTemperature", "REFERENCE_TEMPERATURE", "Temperature", "degC", 4408652),
        new("North.Utilities.P101.ScenarioVibrationRms", "SCENARIO_VIBRATION", "Vibration", "mm/s", 4403510)
    ];

    public ScenarioPoint[] Definitions => Points;

    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly ConcurrentQueue<ScenarioObservation> generated = new();

    // PostgreSQL stores microseconds; keep exact source-time evidence on round trips.
    public DateTimeOffset StartedAt { get; } = MicrosecondUtcNow();

    public double ElapsedSeconds => clock.Elapsed.TotalSeconds;

    public ScenarioObservation[] Generated => generated.ToArray();

    private static DateTimeOffset MicrosecondUtcNow()
    {
        var now = DateTimeOffset.UtcNow;
        return new DateTimeOffset(now.Ticks / 10 * 10, TimeSpan.Zero);
    }

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
        var value = reliability && point == 0
            ? ReliabilityStopSeconds.Any(stop => second >= stop - 16 && second < stop + 7) ? 90 : 60
            : mixedQuality && point == 2 && second >= 8 ? 12.6
                : ValueAt(point, LiveHeating ? Math.Min(39, second / 5) : second);
        var bad = point == 0 && (mixedQuality || reliability && second is >= ReliabilityBadFromSecond and < ReliabilityBadToSecond);
        var observation = new ScenarioObservation(Points[point].Identifier, second,
            value, StartedAt.AddSeconds(second), bad);
        generated.Enqueue(observation);
        return observation;
    }
}

internal sealed record ScenarioPoint(string Identifier, string Code, string Quantity, string Unit, int UnitId);

internal sealed record ScenarioObservation(string Identifier, int Second, double Value, DateTimeOffset SourceTimestamp, bool IsBad);
