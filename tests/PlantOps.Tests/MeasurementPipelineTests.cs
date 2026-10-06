using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using PlantOps.Application.Telemetry;
using Xunit;

namespace PlantOps.Tests;

public sealed class MeasurementPipelineTests
{
    private sealed class Source(string kind = "Simulation") : IMeasurementSource
    {
        public string Kind => kind;

        public Channel<NormalizedSample> Input { get; } = Channel.CreateUnbounded<NormalizedSample>();

        public async IAsyncEnumerable<NormalizedSample> ReadAsync(IReadOnlyList<SourceConfiguration> configurations,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var sample in Input.Reader.ReadAllAsync(cancellationToken))
            {
                yield return sample;
            }
        }
    }

    private static NormalizedSample Sample(SourceConfiguration c, int number) =>
        SamplePolicy.Normalize(c, number, "Good", DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow, number.ToString());

    [Fact]
    public async Task PersistentLowRateStreamFlushesWithoutEndingEnumeration()
    {
        var source = new Source();
        var c = SamplePolicyTests.Configuration;
        var committed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new MeasurementSourceRunner(source, [c], (batch, _) =>
        {
            committed.TrySetResult(batch.Count);
            return Task.CompletedTask;
        },
            (_, _) => Task.CompletedTask, _ => { }, TimeProvider.System);
        using var stop = new CancellationTokenSource();
        var task = runner.RunAsync(stop.Token);
        source.Input.Writer.TryWrite(Sample(c, 1));
        Assert.Equal(1, await committed.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(task.IsCompleted);
        stop.Cancel();
        await task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task FullBatchFlushesBeforeLongTimerAndOtherSourceDoesNotStarve()
    {
        var first = new Source();
        var second = new Source("FakeOther");
        var c = SamplePolicyTests.Configuration;
        var other = c with
        {
            SourceKind = second.Kind,
            DataSourceId = Guid.NewGuid(),
            PointId = Guid.NewGuid()
        };
        var large = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var small = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var a = new MeasurementSourceRunner(first, [c], (rows, _) =>
        {
            large.TrySetResult(rows.Count);
            return Task.CompletedTask;
        },
            (_, _) => Task.CompletedTask, _ => { }, TimeProvider.System, flushInterval: TimeSpan.FromSeconds(10));
        var b = new MeasurementSourceRunner(second, [other], (rows, _) =>
        {
            small.TrySetResult(rows.Count);
            return Task.CompletedTask;
        },
            (_, _) => Task.CompletedTask, _ => { }, TimeProvider.System);
        using var stop = new CancellationTokenSource();
        var tasks = new[] { a.RunAsync(stop.Token), b.RunAsync(stop.Token) };
        for (var i = 0; i < 500; i++)
        {
            first.Input.Writer.TryWrite(Sample(c, i));
        }

        second.Input.Writer.TryWrite(Sample(other, 1));
        Assert.Equal(500, await large.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, await small.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        stop.Cancel();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task OverflowIsCountedAndLossRetryUsesSameEpisodeAndAbsoluteCount()
    {
        var source = new Source();
        var c = SamplePolicyTests.Configuration;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var persisted = new TaskCompletionSource<IngestionLoss>(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = new ConcurrentQueue<IngestionLoss>();
        var commits = 0;
        var lossAttempts = 0;
        var runner = new MeasurementSourceRunner(source, [c], async (_, ct) =>
        {
            if (Interlocked.Increment(ref commits) == 1)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
            }
        },
        (rows, _) =>
        {
            foreach (var loss in rows)
            {
                attempts.Enqueue(loss);
            }

            if (Interlocked.Increment(ref lossAttempts) == 1)
            {
                throw new IOException("Offline loss store");
            }

            persisted.TrySetResult(rows.Single(x => x.Reason == "QueueOverflow"));
            return Task.CompletedTask;
        }, _ => { }, TimeProvider.System, capacity: 2, batchSize: 1);
        using var stop = new CancellationTokenSource();
        var task = runner.RunAsync(stop.Token);
        source.Input.Writer.TryWrite(Sample(c, 0));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        for (var i = 1; i <= 100; i++)
        {
            source.Input.Writer.TryWrite(Sample(c, i));
        }
        // Let the producer consume the source's input while the sink remains blocked.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (source.Input.Reader.Count > 0)
        {
            await Task.Delay(5, timeout.Token);
        }

        release.TrySetResult();
        var loss = await persisted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(98, loss.DroppedCount);
        Assert.False(loss.IsPossible);
        Assert.All(attempts, x => Assert.Equal(loss.Id, x.Id));
        stop.Cancel();
        await task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void LossAcknowledgementCannotEraseConcurrentDropAndOpenEpisodeRecoversWithSameId()
    {
        var c = SamplePolicyTests.Configuration;
        var at = DateTimeOffset.UtcNow;
        var buffer = new IngestionLossBuffer();
        buffer.Record(c, at, at.AddSeconds(1), "QueueOverflow", 1);
        var first = buffer.Snapshot();
        buffer.Record(c, at, at.AddSeconds(2), "QueueOverflow", 1);
        buffer.Acknowledge(first);
        Assert.Equal(2, Assert.Single(buffer.Snapshot()).DroppedCount);
        buffer.Acknowledge(buffer.Snapshot());
        Assert.Empty(buffer.Snapshot());
        buffer.Record(c, at, null, "CollectionFailure", possible: true);
        var open = Assert.Single(buffer.Snapshot());
        buffer.Acknowledge([open]);
        Assert.Empty(buffer.Snapshot());
        buffer.Record(c, at.AddSeconds(1), null, "CollectionFailure", possible: true);
        Assert.Empty(buffer.Snapshot());
        buffer.Recover([c], at.AddSeconds(3));
        var recovered = Assert.Single(buffer.Snapshot());
        Assert.Equal(open.Id, recovered.Id);
        Assert.Equal(at.AddSeconds(3), recovered.To);
    }
}
