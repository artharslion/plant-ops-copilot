using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using PlantOps.Application.Telemetry;
using PlantOps.Integrations.OpcUa;
using Xunit;

namespace PlantOps.Tests;

public sealed class OpcUaRuntimeTests
{
    internal static readonly OpcUaApprovedRuntimeEndpoint Endpoint = new("opc.tcp://127.0.0.1:48481", "urn:industrial-sim:server",
        "urn:industrial-sim:runtime", ["String:plantops-p101/vibration", "String:plantops-p102/vibration"], "artifacts/runtime-test/certificates");

    internal static SourceConfiguration Configuration(string identifier = "plantops-p101/vibration")
    {
        var mapping = new OpcUaMappingInput(Endpoint.NamespaceUri, "String", identifier);
        return SamplePolicyTests.Configuration with
        {
            SourceKind = "OpcUa",
            SourceParameters = JsonSerializer.Serialize(mapping),
            SourceKey = OpcUaConfigurationPolicy.Key(mapping),
            ConnectionParameters = JsonSerializer.Serialize(new OpcUaConnectionInput("local-runtime"))
        };
    }

    [Theory]
    [InlineData(false, true, "opc.tcp://127.0.0.1:48481")]
    [InlineData(true, false, "opc.tcp://127.0.0.1:48481")]
    [InlineData(true, true, "opc.tcp://host.docker.internal:48481")]
    [InlineData(true, true, "opc.tcp://192.168.1.2:48481")]
    [InlineData(true, true, "opc.tcp://localhost.evil:48481")]
    [InlineData(true, true, "opc.tcp://user@localhost:48481")]
    public void RuntimePolicyRejectsUnsafeDeployment(bool development, bool enabled, string url) =>
        Assert.Throws<SourceConfigurationException>(() => OpcUaRuntimePolicy.ValidateEndpoint(Endpoint with { Url = url }, development, enabled));

    [Fact]
    public void ExactNodeAndTotalMappingBudgetAreEnforced()
    {
        var first = Configuration();
        var second = Configuration("plantops-p102/vibration") with
        {
            PointId = Guid.NewGuid(),
            MappingId = Guid.NewGuid()
        };
        Assert.Equal(2, OpcUaRuntimePolicy.Validate([first, second], Endpoint).Length);
        Assert.Throws<SourceConfigurationException>(() => OpcUaRuntimePolicy.Validate([Configuration("plantops-p103/vibration")], Endpoint));
        Assert.Throws<SourceConfigurationException>(() => OpcUaRuntimePolicy.Validate([first, first], Endpoint));
        Assert.Throws<SourceConfigurationException>(() => OpcUaRuntimePolicy.Validate(Enumerable.Range(0, 11)
            .Select(_ => first with { PointId = Guid.NewGuid(), MappingId = Guid.NewGuid() }).ToArray(), Endpoint));
    }

    private sealed class Connection(SourceConfiguration[] configurations) : IOpcUaConnection
    {
        public Channel<NormalizedSample> Input { get; } = Channel.CreateUnbounded<NormalizedSample>();

        public bool Disposed
        {
            get; private set;
        }

        public IReadOnlyList<SamplingRevision> Revisions
        {
            get;
        }
        = configurations.Select(configuration => new SamplingRevision(
            configuration.PointId, configuration.MappingId, configuration.MappingVersion, DateTimeOffset.UtcNow,
            "Periodic", 1000, 1000, 10)).ToArray();

        public DateTimeOffset? LastKeepAliveAt => DateTimeOffset.UtcNow;

        public async IAsyncEnumerable<NormalizedSample> ReadAsync([EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var sample in Input.Reader.ReadAllAsync(ct))
            {
                yield return sample;
            }
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Connector(Func<int, IOpcUaConnection> connect) : IOpcUaConnector
    {
        public int Attempts;

        public Task<IOpcUaConnection> ConnectAsync(IReadOnlyList<SourceConfiguration> configurations, CancellationToken ct) =>
            Task.FromResult(connect(Interlocked.Increment(ref Attempts)));
    }

    private sealed class Store : ISourceRuntimeStore
    {
        public ConcurrentQueue<SourceHealth> States { get; } = new();

        public int Opened;

        public int Closed;

        public int Losses;

        public bool RejectActivation;

        public Task ActivateAsync(IReadOnlyList<SourceConfiguration> configurations, IReadOnlyList<SamplingRevision> revisions, CancellationToken ct)
        {
            if (RejectActivation)
            {
                throw new SourceConfigurationException("ConfigurationChanged");
            }
            Interlocked.Increment(ref Opened);
            return Task.CompletedTask;
        }

        public Task CloseAsync(IReadOnlyList<SamplingRevision> revisions, DateTimeOffset at, CancellationToken ct)
        {
            Interlocked.Increment(ref Closed);
            return Task.CompletedTask;
        }

        public Task ReportAsync(SourceHealth health, CancellationToken ct)
        {
            States.Enqueue(health);
            return Task.CompletedTask;
        }

        public Task RecordLossAsync(IReadOnlyList<SourceConfiguration> configurations, DateTimeOffset at, string reason, CancellationToken ct)
        {
            Interlocked.Increment(ref Losses);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task MultiPointDisconnectRebuildsClosesProfilesAndKeepsHeartbeatForFrozenNodes()
    {
        var first = Configuration();
        var second = first with
        {
            PointId = Guid.NewGuid(),
            MappingId = Guid.NewGuid()
        };
        var before = new Connection([first, second]);
        var after = new Connection([first, second]);
        var connector = new Connector(attempt => attempt == 1 ? before : after);
        var store = new Store();
        var source = new OpcUaRuntimeSource(connector, store, TimeProvider.System);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var received = new ConcurrentQueue<NormalizedSample>();
        var run = Task.Run(async () =>
        {
            try
            {
                await foreach (var sample in source.ReadAsync([first, second], stop.Token))
                {
                    Assert.True(store.Opened > 0);
                    received.Enqueue(sample);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        });
        var now = DateTimeOffset.UtcNow;
        before.Input.Writer.TryWrite(SamplePolicy.Normalize(first, 4, "Good", now, null, now, "first"));
        before.Input.Writer.TryWrite(SamplePolicy.Normalize(second, 5, "Uncertain", now, null, now, "second"));
        before.Input.Writer.TryComplete(new IOException("network"));
        while (store.States.Count(state => state.State == "Connected" && state.ReconnectCount == 1) < 2)
        {
            await Task.Delay(20, stop.Token);
        }
        var reconnected = store.States.Where(state => state.State == "Connected" && state.ReconnectCount == 1).ToArray();
        Assert.NotNull(reconnected[0].LastKeepAliveAt);
        Assert.True(reconnected[^1].LastKeepAliveAt > reconnected[0].LastKeepAliveAt);
        Assert.All(reconnected, state => Assert.Equal(now, state.LastReceivedAt));
        Assert.True(before.Disposed);
        Assert.Equal(1, store.Closed);
        Assert.Equal(1, store.Losses);
        Assert.Equal(2, received.Count);
        Assert.Contains(received, sample => sample.Quality == "Uncertain");
        Assert.Contains(store.States, state => state.State == "Reconnecting");
        Assert.True(store.States.Count(state => state.State == "Connected") >= 3);
        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(after.Disposed);
        Assert.Equal(2, store.Closed);
        Assert.Equal("Stopped", store.States.Last().State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfigurationOrSecurityFailureLatchesWithoutEmittingOrRetrying(bool rejectActivation)
    {
        var configuration = Configuration();
        var connection = new Connection([configuration]);
        var store = new Store { RejectActivation = rejectActivation };
        var connector = new Connector(_ => rejectActivation ? connection : throw new SourceConfigurationException("SecurityRejected"));
        var source = new OpcUaRuntimeSource(connector, store, TimeProvider.System);
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(1600));
        var count = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var sample in source.ReadAsync([configuration], stop.Token))
            {
                count++;
            }
        });
        Assert.Equal(0, count);
        Assert.Equal(1, connector.Attempts);
        Assert.Equal(0, store.Opened);
        Assert.Equal(0, store.Closed);
        Assert.Contains(store.States, state => state.State == "Faulted");
        Assert.True(!rejectActivation || connection.Disposed);
    }

    [Fact]
    public async Task ContinuousNotificationsDoNotStarveConnectionHeartbeat()
    {
        var configuration = Configuration();
        var connection = new Connection([configuration]);
        var store = new Store();
        var source = new OpcUaRuntimeSource(new Connector(_ => connection), store, TimeProvider.System);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var read = Task.Run(async () =>
        {
            try
            {
                await foreach (var sample in source.ReadAsync([configuration], stop.Token))
                {
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        });
        for (var index = 0; index < 30; index++)
        {
            var at = DateTimeOffset.UtcNow;
            connection.Input.Writer.TryWrite(SamplePolicy.Normalize(configuration, 4, "Good", at, null, at, index.ToString()));
            await Task.Delay(100, stop.Token);
        }
        Assert.True(store.States.Count(state => state.State == "Connected") >= 2);
        Assert.Contains(store.States, state => state.State == "Connected" && state.LastReceivedAt != null);
        stop.Cancel();
        await read.WaitAsync(TimeSpan.FromSeconds(2));
    }
}
