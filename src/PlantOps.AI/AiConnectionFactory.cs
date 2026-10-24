using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace PlantOps.AI;

// DNS retries happen before returning a connected stream to HttpClient: no
// HTTP request body has been transmitted and no model call is replayed.
internal sealed class AiConnectionFactory(
    Func<string, CancellationToken, Task<IPAddress[]>> resolve,
    Func<TimeSpan, CancellationToken, Task> delay,
    ILogger? logger = null)
{
    internal async Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await resolve(host, ct);
            }
            catch (SocketException exception) when (exception.SocketErrorCode == SocketError.TryAgain && attempt < 3)
            {
                logger?.LogWarning("AI connection: stage=dns-transient-retry; attempt={Attempt}; error={Error}",
                    attempt, exception.SocketErrorCode);
                await delay(TimeSpan.FromMilliseconds(attempt * 300), ct);
            }
        }
    }

    internal async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var addresses = await ResolveAsync(context.DnsEndPoint.Host, ct);
        if (addresses.Length == 0)
        {
            throw new SocketException((int)SocketError.HostNotFound);
        }
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
