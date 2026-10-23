using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using PlantOps.AI;
using PlantOps.Application.AI;
using Xunit;

namespace PlantOps.Tests;

public sealed class AiStreamingTests
{
    private static string Chunk(object delta, string? finish = null) => "data: " + JsonSerializer.Serialize(new
    {
        model = "gpt-6.1-sol",
        choices = new[] { new { index = 0, delta, finish_reason = finish } }
    }) + "\n\n";

    private const string Usage = "data: {\"model\":\"gpt-6.1-sol\",\"choices\":[],\"usage\":{\"prompt_tokens\":80,\"completion_tokens\":9}}\n\n";
    private const string Done = "data: [DONE]\n\n";

    [Fact]
    public async Task RepeatedSseMetadataDoesNotConsumeAssembledAnswerLimit()
    {
        var payload = string.Concat(Enumerable.Repeat(Chunk(new
        {
            content = "振"
        }), 200))
            + Chunk(new
            {
            }, "stop") + Usage + Done;
        Assert.True(Encoding.UTF8.GetByteCount(payload) > 4096);
        using var client = new HttpClient(new Handler(payload));
        using var budget = new LlmBudget();
        var result = await Gateway(client, maximumBytes: 4096).CompleteAsync(new([new(LlmRole.User, "测试")]), budget);
        Assert.Equal(new string('振', 200), result.Content);
    }

    [Fact]
    public async Task ExcessiveSseFramingIsStillBounded()
    {
        var payload = string.Concat(Enumerable.Repeat(": " + new string('x', 200) + "\n\n", 50))
            + Chunk(new
            {
                content = "ok"
            }, "stop") + Done;
        using var client = new HttpClient(new Handler(payload));
        using var budget = new LlmBudget();
        var error = await Assert.ThrowsAsync<LlmException>(() => Gateway(client, maximumBytes: 1024)
            .CompleteAsync(new([new(LlmRole.User, "测试")]), budget));
        Assert.Equal(LlmFailure.InvalidResponse, error.Failure);
    }

    [Fact]
    public async Task FragmentedUtf8TextAndUsageFinishBeforeConnectionCloses()
    {
        using var client = new HttpClient(new Handler(Chunk(new
        {
            role = "assistant",
            content = "振动"
        }) + Chunk(new
        {
            content = "证据。"
        }, "stop") + Usage + Done, stallAtEnd: true));
        using var budget = new LlmBudget();
        var result = await Gateway(client).CompleteAsync(new([new(LlmRole.User, "测试")]), budget);
        Assert.Equal("振动证据。", result.Content);
        Assert.Equal(new LlmUsage(80, 9), result.Usage);
        Assert.Equal(80, budget.ReservedInputTokens);
        Assert.Equal(9, budget.ReservedOutputTokens);
    }

    [Fact]
    public async Task ToolArgumentDeltasAssembleBeforeValidation()
    {
        var payload = Chunk(new
        {
            tool_calls = new[] { new { index = 0, id = "call1", type = "function", function = new { name = "search_documents", arguments = "{\"query\":" } } }
        })
            + Chunk(new
            {
                tool_calls = new[] { new { index = 0, function = new { arguments = "\"振动\",\"limit\":1}" } } }
            }, "tool_calls") + Usage + Done;
        using var schema = JsonDocument.Parse("""{"type":"object"}""");
        using var client = new HttpClient(new Handler(payload));
        using var budget = new LlmBudget();
        var result = await Gateway(client).CompleteAsync(new([new(LlmRole.User, "测试")], [new("search_documents", "Read only", schema.RootElement)]), budget);
        var call = Assert.Single(result.ToolCalls);
        InvestigationEngine.ValidateTool(call.Name, JsonDocument.Parse(call.Arguments).RootElement);
        Assert.Equal("call1", call.Id);
        Assert.Equal(1, budget.ToolCalls);
    }

    [Theory]
    [InlineData("missing-done", LlmFailure.InvalidResponse)]
    [InlineData("malformed", LlmFailure.InvalidResponse)]
    [InlineData("truncated", LlmFailure.Truncated)]
    [InlineData("refused", LlmFailure.Refused)]
    [InlineData("oversized", LlmFailure.InvalidResponse)]
    public async Task IncompleteOrInvalidStreamsNeverReturnPartialAnswers(string scenario, LlmFailure expected)
    {
        var payload = scenario switch
        {
            "missing-done" => Chunk(new { content = "partial" }, "stop"),
            "malformed" => "data: {invalid}\n\n" + Done,
            "truncated" => Chunk(new { content = "partial" }, "length") + Done,
            "refused" => Chunk(new { refusal = "refused" }, "stop") + Done,
            _ => Chunk(new { content = new string('x', 2048) }, "stop") + Done
        };
        using var client = new HttpClient(new Handler(payload));
        using var budget = new LlmBudget();
        var error = await Assert.ThrowsAsync<LlmException>(() => Gateway(client, maximumBytes: scenario == "oversized" ? 1024 : 131072)
            .CompleteAsync(new([new(LlmRole.User, "测试")]), budget));
        Assert.Equal(expected, error.Failure);
        Assert.Equal(1, budget.Attempts);
    }

    [Fact]
    public async Task CancellationInterruptsStalledStreamAfterPartialText()
    {
        using var client = new HttpClient(new Handler(Chunk(new
        {
            content = "partial"
        }), stallAtEnd: true));
        using var budget = new LlmBudget();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Gateway(client).CompleteAsync(new([new(LlmRole.User, "测试")]), budget, cancellation.Token));
    }

    [Fact]
    public async Task InvalidStreamDiagnosticsContainStageAndCountersWithoutProviderText()
    {
        const string privateText = "provider-private-content";
        using var client = new HttpClient(new Handler("data: {" + privateText + "}\n\n"));
        using var budget = new LlmBudget();
        var logger = new CapturedLogger();
        var gateway = new OpenAiCompatibleGateway(client, Options.Create(new AiOptions
        {
            Provider = "OpenAiCompatible",
            ApiKey = "private-test-key",
            StreamResponses = true,
            MaxRetries = 0
        }), logger);
        await Assert.ThrowsAsync<LlmException>(() => gateway.CompleteAsync(new([new(LlmRole.User, "测试")]), budget));
        var entry = Assert.Single(logger.Entries, x => x.Contains("AI stream invalid:"));
        Assert.Contains("stage=event-json", entry);
        Assert.Contains("events=1", entry);
        Assert.DoesNotContain(privateText, entry);
        Assert.DoesNotContain("private-test-key", entry);
    }

    [Fact]
    public async Task ActiveStreamCanOutliveRequestIdleTimeout()
    {
        var chunks = Enumerable.Repeat(Chunk(new
        {
            content = "ok"
        }), 6)
            .Append(Chunk(new
            {
            }, "stop") + Usage + Done).ToArray();
        using var client = new HttpClient(new TimedHandler(chunks));
        using var budget = new LlmBudget(new(RunTimeoutSeconds: 5));
        var logger = new CapturedLogger();
        var result = await TimedGateway(client, logger).CompleteAsync(new([new(LlmRole.User, "test")]), budget);
        Assert.Equal(string.Concat(Enumerable.Repeat("ok", 6)), result.Content);
        Assert.Contains(logger.Entries, x => x.Contains("stage=receiving"));
        Assert.Contains(logger.Entries, x => x.Contains("stage=validated"));
        Assert.DoesNotContain(logger.Entries, x => x.Contains("private-test-key"));
        Assert.Equal(1, budget.Attempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamStillStopsForIdleOrIndependentRunDeadline(bool activeUntilDeadline)
    {
        var chunks = activeUntilDeadline
            ? Enumerable.Repeat(Chunk(new
            {
                content = "partial"
            }), 20).ToArray()
            : new[] { Chunk(new { content = "partial" }) };
        using var client = new HttpClient(new TimedHandler(chunks));
        using var budget = new LlmBudget(new(RunTimeoutSeconds: activeUntilDeadline ? 1 : 5));
        var logger = new CapturedLogger();
        var error = await Assert.ThrowsAsync<LlmException>(() => TimedGateway(client, logger)
            .CompleteAsync(new([new(LlmRole.User, "test")]), budget));
        Assert.Equal(LlmFailure.Timeout, error.Failure);
        Assert.Equal(activeUntilDeadline, budget.Deadline.IsCancellationRequested);
        Assert.Contains(logger.Entries, x => x.Contains("AI stream stopped:") && x.Contains("idleMs="));
        Assert.DoesNotContain(logger.Entries, x => x.Contains("stage=validated") || x.Contains("partial"));
        Assert.Equal(1, budget.Attempts);
    }

    private static OpenAiCompatibleGateway TimedGateway(HttpClient client, CapturedLogger logger) => new(client, Options.Create(new AiOptions
    {
        Provider = "OpenAiCompatible",
        ApiKey = "private-test-key",
        StreamResponses = true,
        RequestTimeoutSeconds = 1,
        RunTimeoutSeconds = 5,
        MaxRetries = 1
    }), logger);

    private sealed class TimedHandler(string[] chunks) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new StreamContent(new TimedStream(chunks));
            content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class TimedStream(string[] chunks) : MemoryStream
    {
        private int next;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (next == chunks.Length)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            await Task.Delay(300, cancellationToken);
            var bytes = Encoding.UTF8.GetBytes(chunks[next++]);
            bytes.AsMemory().CopyTo(buffer);
            return bytes.Length;
        }
    }

    private sealed class CapturedLogger : ILogger<OpenAiCompatibleGateway>
    {
        public List<string> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add(formatter(state, exception));
    }

    private static OpenAiCompatibleGateway Gateway(HttpClient client, int maximumBytes = 131072) => new(client, Options.Create(new AiOptions
    {
        Provider = "OpenAiCompatible",
        ApiKey = "test-only",
        StreamResponses = true,
        MaxRetries = 0,
        MaxResponseBytes = maximumBytes
    }));

    private sealed class Handler(string data, bool stallAtEnd = false) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
            Assert.True(body.RootElement.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
            var content = new StreamContent(new FragmentedStream(Encoding.UTF8.GetBytes(data), stallAtEnd));
            content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return new(HttpStatusCode.OK)
            {
                Content = content
            };
        }
    }

    private sealed class FragmentedStream(byte[] data, bool stallAtEnd) : MemoryStream(data)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (stallAtEnd && Position == Length)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            return await base.ReadAsync(buffer[..Math.Min(3, buffer.Length)], cancellationToken);
        }
    }
}
