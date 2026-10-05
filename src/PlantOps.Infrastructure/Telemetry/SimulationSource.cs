using System.Runtime.CompilerServices;
using System.Text.Json;
using PlantOps.Application.Telemetry;

namespace PlantOps.Infrastructure.Telemetry;

public sealed class SimulationSource(TimeProvider clock) : IMeasurementSource
{
    public string Kind => "Simulation";
    public static NormalizedSample SampleAt(SourceConfiguration config, DateTimeOffset sourceAt, DateTimeOffset receivedAt)
    {
        using var parameters = JsonDocument.Parse(config.SourceParameters);
        if (parameters.RootElement.GetProperty("schemaVersion").GetInt32() != 1
            || parameters.RootElement.GetProperty("scenario").GetString() != "GoldenPath")
            throw new InvalidOperationException("Unsupported simulation scenario.");
        var second = ((sourceAt.ToUnixTimeSeconds() % 120) + 120) % 120;
        var value = second < 30 ? 4.0 : second < 60 ? 12.6 : 6.0;
        var quality = second is >= 60 and < 70 ? "Bad" : second is >= 70 and < 80 ? "Uncertain" : "Good";
        var key = $"{config.MappingId:N}:{config.MappingVersion}:{config.SourceVersion}:{sourceAt.UtcTicks}";
        return SamplePolicy.Normalize(config, value, quality, sourceAt, sourceAt, receivedAt, key,
            symbol: quality == "Good" ? "SimulatedGood" : $"Simulated{quality}");
    }
    public async IAsyncEnumerable<NormalizedSample> ReadAsync(IReadOnlyList<SourceConfiguration> configurations,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var receivedAt = clock.GetUtcNow();
        var sourceAt = DateTimeOffset.FromUnixTimeSeconds(receivedAt.ToUnixTimeSeconds());
        foreach (var config in configurations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sourceAt.ToUnixTimeMilliseconds() % config.ExpectedIntervalMs < 1000)
                yield return SampleAt(config, sourceAt, receivedAt);
        }
        await Task.CompletedTask;
    }
}
