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
        var entry = Assert.Single(logger.Entries);
        Assert.Contains("stage=event-json", entry);
        Assert.Contains("events=1", entry);
        Assert.DoesNotContain(privateText, entry);
        Assert.DoesNotContain("private-test-key", entry);
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
