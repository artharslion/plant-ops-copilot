using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PlantOps.AI;
using PlantOps.Application.AI;
using Xunit;

namespace PlantOps.Tests;

public sealed class AiTransportDiagnosticTests
{
    [Theory]
    [InlineData("status", "stage=http-status; status=503")]
    [InlineData("network", "stage=http-transport; error=NameResolutionError")]
    [InlineData("io", "stage=io; headers=False")]
    public async Task SafeDiagnosticsDistinguishFailureWithoutSecretsOrRetry(string mode, string expected)
    {
        const string secret = "private-key-body-endpoint-marker";
        using var handler = new FailingHandler(mode, secret);
        using var client = new HttpClient(handler);
        var logger = new CapturedAiGatewayLogger();
        var gateway = new OpenAiCompatibleGateway(client, Options.Create(new AiOptions
        {
            Provider = "OpenAiCompatible",
            ApiKey = secret,
            MaxRetries = 0
        }), logger);
        using var budget = new LlmBudget();
        var failure = await Assert.ThrowsAsync<LlmException>(() => gateway.CompleteAsync(new([new(LlmRole.User, secret)]), budget));
        Assert.Equal(LlmFailure.Unavailable, failure.Failure);
        Assert.Equal(1, handler.Calls);
        var entry = Assert.Single(logger.Entries);
        Assert.Contains(expected, entry);
        Assert.DoesNotContain(secret, entry);
        Assert.DoesNotContain("https://", entry);
        Assert.DoesNotContain(secret, failure.ToString());
    }

    private sealed class FailingHandler(string mode, string secret) : HttpMessageHandler
    {
        public int Calls
        {
            get; private set;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return mode switch
            {
                "network" => throw new HttpRequestException(HttpRequestError.NameResolutionError, secret),
                "io" => throw new IOException(secret),
                _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent(secret) })
            };
        }
    }
}

internal sealed class CapturedAiGatewayLogger : ILogger<OpenAiCompatibleGateway>
{
    public List<string> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (IsEnabled(logLevel) && Entries.Count < 32)
        {
            Entries.Add(formatter(state, exception));
        }
    }
}
