using System.Runtime.CompilerServices;
using PlantOps.Application.Telemetry;

namespace PlantOps.Integrations.OpcUa;

// One invocation owns one source lifecycle; no SDK state is shared between sources.
public sealed class OpcUaRuntimeSource(IOpcUaConnector connector, ISourceRuntimeStore store, TimeProvider clock) : IMeasurementSource
{
    public string Kind => "OpcUa";

    public async IAsyncEnumerable<NormalizedSample> ReadAsync(IReadOnlyList<SourceConfiguration> configurations,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (configurations.Count is < 1 or > 200 || configurations.Any(x => x.SourceKind != Kind)
            || configurations.Select(x => x.DataSourceId).Distinct().Count() != 1)
        {
            throw new ArgumentException("A runtime requires one bounded OPC UA source.");
        }
        var configuration = configurations[0];
        var reconnects = 0;
        var failures = 0;
        DateTimeOffset? lastKeepAlive = null;
        DateTimeOffset? lastReceived = null;
        var lossRecorded = false;
        async Task ReportAsync(string state, string? failure, CancellationToken ct) =>
            await store.ReportAsync(new(configuration.DataSourceId, configuration.SourceVersion, state,
                clock.GetUtcNow(), lastKeepAlive, lastReceived, reconnects, failure), ct);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                IOpcUaConnection? connection = null;
                var activated = false;
                Exception? failure = null;
                var connectedAt = clock.GetUtcNow();
                try
                {
                    try
                    {
                        await ReportAsync(reconnects == 0 ? "Connecting" : "Reconnecting", null, cancellationToken);
                        connection = await connector.ConnectAsync(configurations, cancellationToken);
                        await store.ActivateAsync(configurations, connection.Revisions, cancellationToken);
                        activated = true;
                        connectedAt = clock.GetUtcNow();
                        lastKeepAlive = connection.LastKeepAliveAt;
                        await ReportAsync("Connected", null, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception exception) { failure = exception; }

                    if (failure == null)
                    {
                        using var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        await using var reader = connection!.ReadAsync(readCancellation.Token).GetAsyncEnumerator(readCancellation.Token);
                        var next = reader.MoveNextAsync().AsTask();
                        var reportAt = clock.GetUtcNow().AddSeconds(2);
                        try
                        {
                            while (failure == null)
                            {
                                bool available;
                                try
                                {
                                    // Heartbeats continue even when a node's physical value/time is frozen.
                                    using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                                    var tick = Task.Delay(TimeSpan.FromSeconds(2), clock, heartbeat.Token);
                                    if (await Task.WhenAny(next, tick) == tick)
                                    {
                                        cancellationToken.ThrowIfCancellationRequested();
                                        lastKeepAlive = connection.LastKeepAliveAt;
                                        if (lastKeepAlive == null || clock.GetUtcNow() - lastKeepAlive > TimeSpan.FromSeconds(10))
                                        {
                                            throw new IOException("Session keep-alive expired.");
                                        }
                                        await ReportAsync("Connected", null, cancellationToken);
                                        reportAt = clock.GetUtcNow().AddSeconds(2);
                                        continue;
                                    }
                                    heartbeat.Cancel();
                                    available = await next;
                                    if (!available)
                                    {
                                        throw new IOException("OPC UA stream ended.");
                                    }
                                    lastKeepAlive = connection.LastKeepAliveAt;
                                    lastReceived = reader.Current.ReceivedAt;
                                    lossRecorded = false;
                                    if (clock.GetUtcNow() >= reportAt)
                                    {
                                        await ReportAsync("Connected", null, cancellationToken);
                                        reportAt = clock.GetUtcNow().AddSeconds(2);
                                    }
                                }
                                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                                catch (Exception exception)
                                {
                                    failure = exception;
                                    break;
                                }
                                yield return reader.Current;
                                next = reader.MoveNextAsync().AsTask();
                            }
                        }
                        finally
                        {
                            readCancellation.Cancel();
                            // Dispose only after the single outstanding MoveNext has observed cancellation.
                            try
                            {
                                await next;
                            }
                            catch (Exception) { }
                        }
                    }
                }
                finally
                {
                    // Close the observed profile even if SDK cleanup itself fails.
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try
                    {
                        if (activated)
                        {
                            await store.CloseAsync(connection!.Revisions, clock.GetUtcNow(), cleanup.Token);
                        }
                    }
                    finally
                    {
                        if (connection != null)
                        {
                            await connection.DisposeAsync();
                        }
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (!lossRecorded)
                {
                    // Older queued samples must not close this episode before the connection recovers.
                    await store.RecordLossAsync(configurations, clock.GetUtcNow(), "OpcUaDisconnected", cancellationToken);
                    lossRecorded = true;
                }
                if (failure is SourceConfigurationException configurationFailure)
                {
                    // Latch until Worker configuration replacement/cancellation. Never security-downgrade or hot retry.
                    while (true)
                    {
                        await ReportAsync("Faulted", configurationFailure.Message, cancellationToken);
                        await Task.Delay(TimeSpan.FromSeconds(5), clock, cancellationToken);
                    }
                }
                reconnects++;
                failures = activated && clock.GetUtcNow() - connectedAt >= TimeSpan.FromSeconds(10) ? 1 : Math.Min(failures + 1, 6);
                await ReportAsync("Reconnecting", "ConnectionLost", cancellationToken);
                var seconds = Math.Min(30, Math.Pow(2, failures - 1) * (0.8 + Random.Shared.NextDouble() * 0.4));
                var retryAt = clock.GetUtcNow().AddSeconds(seconds);
                while (clock.GetUtcNow() < retryAt)
                {
                    var remaining = retryAt - clock.GetUtcNow();
                    if (remaining <= TimeSpan.Zero)
                    {
                        break;
                    }
                    await Task.Delay(remaining < TimeSpan.FromSeconds(2) ? remaining : TimeSpan.FromSeconds(2), clock, cancellationToken);
                    await ReportAsync("Reconnecting", "ConnectionLost", cancellationToken);
                }
            }
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await ReportAsync("Stopped", null, cleanup.Token);
        }
    }
}
