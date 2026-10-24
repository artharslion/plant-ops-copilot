using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Options;
using PlantOps.AI;
using PlantOps.Application.AI;
using Xunit;

namespace PlantOps.Tests;

public sealed class AiConnectionTests
{
    [Theory]
    [InlineData(SocketError.TryAgain, 3)]
    [InlineData(SocketError.HostNotFound, 1)]
    public async Task DnsRetriesAreBoundedAndOnlyForTemporaryFailure(SocketError error, int expected)
    {
        var lookups = 0;
        var pauses = 0;
        var factory = new AiConnectionFactory((_, _) =>
        {
            lookups++;
            throw new SocketException((int)error);
        }, (_, _) =>
        {
            pauses++;
            return Task.CompletedTask;
        });
        var failure = await Assert.ThrowsAsync<SocketException>(() => factory.ResolveAsync("test.invalid", CancellationToken.None));
        Assert.Equal(error, failure.SocketErrorCode);
        Assert.Equal(expected, lookups);
        Assert.Equal(expected - 1, pauses);
    }

    [Fact]
    public async Task CancellationStopsDnsBackoffBeforeAnotherLookup()
    {
        using var cancel = new CancellationTokenSource();
        var lookups = 0;
        var factory = new AiConnectionFactory((_, _) =>
        {
            lookups++;
            throw new SocketException((int)SocketError.TryAgain);
        }, async (_, ct) =>
        {
            cancel.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => factory.ResolveAsync("test.invalid", cancel.Token));
        Assert.Equal(1, lookups);
    }

    [Fact]
    public async Task TwoTemporaryDnsFailuresStillSendExactlyOneModelPostWithOneReservation()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var posts = 0;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = Task.Run(async () =>
        {
            using var connection = await listener.AcceptTcpClientAsync(deadline.Token);
            await using var stream = connection.GetStream();
            using var header = new MemoryStream();
            var single = new byte[1];
            while (true)
            {
                await stream.ReadExactlyAsync(single, deadline.Token);
                header.WriteByte(single[0]);
                if (header.Length >= 4 && Encoding.ASCII.GetString(header.GetBuffer(), (int)header.Length - 4, 4) == "\r\n\r\n")
                {
                    break;
                }
                Assert.True(header.Length < 16000);
            }
            var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
            Assert.StartsWith("POST /v1/chat/completions HTTP/1.1", lines[0]);
            posts++;
            var length = int.Parse(Assert.Single(lines, x => x.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':')[1], System.Globalization.CultureInfo.InvariantCulture);
            var body = new byte[length];
            await stream.ReadExactlyAsync(body, deadline.Token);
            const string response = """{"model":"test","choices":[{"finish_reason":"stop","message":{"role":"assistant","content":"ok"}}],"usage":{"prompt_tokens":20,"completion_tokens":3}}""";
            var bytes = Encoding.UTF8.GetBytes(response);
            var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(headers, deadline.Token);
            await stream.WriteAsync(bytes, deadline.Token);
        }, deadline.Token);
        var lookups = 0;
        var factory = new AiConnectionFactory((_, _) => ++lookups <= 2
            ? Task.FromException<IPAddress[]>(new SocketException((int)SocketError.TryAgain))
            : Task.FromResult(new[] { IPAddress.Loopback }), (_, _) => Task.CompletedTask);
        using var handler = new SocketsHttpHandler { ConnectCallback = factory.ConnectAsync, UseProxy = false };
        using var client = new HttpClient(handler);
        var gateway = new OpenAiCompatibleGateway(client, Options.Create(new AiOptions
        {
            Provider = "OpenAiCompatible",
            Model = "test",
            BaseUrl = $"http://localhost:{port}/v1",
            ApiKey = "local-test-only"
        }));
        using var budget = new LlmBudget(new(MaxOutputTokens: 6000));
        var result = await gateway.CompleteAsync(new([new(LlmRole.User, "test")], MaxOutputTokens: 6000), budget, deadline.Token);
        await server;
        Assert.Equal("ok", result.Content);
        Assert.Equal(3, lookups);
        Assert.Equal(1, posts);
        Assert.Equal(1, budget.Attempts);
        Assert.Equal(1, budget.ModelRounds);
        Assert.Equal(3, budget.ReservedOutputTokens);
    }
}
