extern alias worker;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PlantOps.Application.Alarms;
using PlantOps.Application.Telemetry;
using MeasurementWorker = worker::PlantOps.Worker.MeasurementWorker;
using Xunit;

namespace PlantOps.Tests;

public sealed class MeasurementWorkerTests
{
    private sealed class Source(string kind) : IMeasurementSource
    {
        public string Kind => kind;
        public int Starts; public int Stops;
        public async IAsyncEnumerable<NormalizedSample> ReadAsync(IReadOnlyList<SourceConfiguration> configurations,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Starts);
            try
            {
                var at = DateTimeOffset.UtcNow;
                yield return SamplePolicy.Normalize(configurations[0], 4, "Good", at, null, at, Guid.NewGuid().ToString("N"));
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally { Interlocked.Increment(ref Stops); }
        }
    }
    private sealed class Configurations(SourceConfiguration[] configurations) : ISourceConfigurationReader
    { public Task<IReadOnlyList<SourceConfiguration>> GetEnabledAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SourceConfiguration>>(configurations); }
    private sealed class Ingestor : IMeasurementIngestor
    {
        public ConcurrentBag<string> Kinds { get; } = [];
        public Task<IngestResult> IngestAsync(IReadOnlyList<NormalizedSample> rows, CancellationToken ct = default)
        { foreach (var row in rows) Kinds.Add(row.Configuration.SourceKind); return Task.FromResult(new IngestResult(rows.Count, 0)); }
    }
    private sealed class Gaps : IIngestionGapRecorder
    {
        public Task RecordAsync(IReadOnlyList<IngestionLoss> losses, CancellationToken ct = default) => Task.CompletedTask;
        public Task RecoverAsync(IReadOnlyList<SourceConfiguration> configurations, DateTimeOffset at, CancellationToken ct = default) => Task.CompletedTask;
        public Task DetectStartupAsync(IReadOnlyList<SourceConfiguration> configurations, CancellationToken ct = default) => Task.CompletedTask;
    }
    private sealed class Reset : IAlarmRuntimeReset { public Task ResetAsync(CancellationToken ct) => Task.CompletedTask; }

    [Fact]
    public async Task SimulationDisableCancelsOnlySimulationAndVersionChangeRebuildsOtherSource()
    {
        var sim = new Source("Simulation"); var other = new Source("Other"); var c = SamplePolicyTests.Configuration;
        var configs = new[] { c, c with { SourceKind = other.Kind, PointId = Guid.NewGuid(), DataSourceId = Guid.NewGuid(), MappingId = Guid.NewGuid() } };
        var reader = new Configurations(configs); var ingest = new Ingestor();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Telemetry:SimulationEnabled"] = "true" }).Build();
        await using var services = new ServiceCollection().AddSingleton<ISourceConfigurationReader>(reader)
            .AddSingleton<IMeasurementIngestor>(ingest).AddSingleton<IIngestionGapRecorder, Gaps>().AddSingleton<IAlarmRuntimeReset, Reset>().BuildServiceProvider();
        using var worker = new MeasurementWorker(services.GetRequiredService<IServiceScopeFactory>(), [sim, other],
            NullLogger<MeasurementWorker>.Instance, config, TimeProvider.System);
        await worker.StartAsync(default);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            while (ingest.Kinds.Distinct().Count() != 2) await Task.Delay(10, timeout.Token);
            config["Telemetry:SimulationEnabled"] = "false";
            while (sim.Stops == 0) await Task.Delay(10, timeout.Token);
            Assert.Equal(0, other.Stops); Assert.Equal(1, other.Starts);
            configs[1] = configs[1] with { MappingVersion = configs[1].MappingVersion + 1 };
            while (other.Starts < 2) await Task.Delay(10, timeout.Token);
            Assert.True(other.Stops >= 1); Assert.Equal(1, sim.Starts);
        }
        finally { await worker.StopAsync(default); }
        Assert.Equal(other.Starts, other.Stops);
    }
}
