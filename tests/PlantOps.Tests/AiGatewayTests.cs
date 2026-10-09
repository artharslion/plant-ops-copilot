using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using PlantOps.AI;
using PlantOps.Application.AI;
using Xunit;

namespace PlantOps.Tests;

public sealed class AiGatewayTests
{
    private const string Secret = "unit-test-private-key";
    private const string Hello = """{"model":"gpt-6.1-sol","choices":[{"finish_reason":"stop","message":{"role":"assistant","content":"Hello."}}],"usage":{"prompt_tokens":20,"completion_tokens":3}}""";
    private static readonly LlmRequest Request = new([new(LlmRole.System, "Read only."), new(LlmRole.User, "Say hello.")]);

    [Fact]
    public async Task SendsCorrectEndpointAuthorizationAndBoundedNonStreamingRequest()
    {
        using var handler = new Handler(async (request, ct) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://zzone.cc.cd/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal(Secret, request.Headers.Authorization.Parameter);
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
            using var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync(ct));
            Assert.Equal("gpt-6.1-sol", body.RootElement.GetProperty("model").GetString());
            Assert.False(body.RootElement.GetProperty("stream").GetBoolean());
            Assert.Equal(500, body.RootElement.GetProperty("max_completion_tokens").GetInt32());
            Assert.Equal("low", body.RootElement.GetProperty("reasoning_effort").GetString());
            Assert.False(body.RootElement.TryGetProperty("api_key", out _));
            Assert.Equal("system", body.RootElement.GetProperty("messages")[0].GetProperty("role").GetString());
            return Response(Hello);
        });
        using var client = new HttpClient(handler);
        using var budget = new LlmBudget();
        var completion = await Gateway(client).CompleteAsync(Request, budget);
        Assert.Equal("Hello.", completion.Content);
        Assert.Equal(new LlmUsage(20, 3), completion.Usage);
        Assert.Equal(1, budget.Attempts);
    }

    [Theory]
    [InlineData(401, LlmFailure.Unauthorized)]
    [InlineData(403, LlmFailure.Unauthorized)]
    [InlineData(400, LlmFailure.UnsupportedRequest)]
    [InlineData(404, LlmFailure.UnsupportedRequest)]
    [InlineData(422, LlmFailure.UnsupportedRequest)]
    [InlineData(429, LlmFailure.RateLimited)]
    [InlineData(503, LlmFailure.Unavailable)]
    [InlineData(302, LlmFailure.InvalidResponse)]
    public async Task FailureDoesNotExposeProviderBodyOrCredential(int status, LlmFailure failure)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response($"SQL and credential {Secret}", (HttpStatusCode)status)));
        using var client = new HttpClient(handler);
        using var budget = new LlmBudget();
        var exception = await Assert.ThrowsAsync<LlmException>(() => Gateway(client).CompleteAsync(Request, budget));
        Assert.Equal(failure, exception.Failure);
        Assert.DoesNotContain(Secret, exception.ToString());
        Assert.Null(exception.InnerException);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task TransientRetryConsumesBothReservations()
    {
        var count = 0;
        using var handler = new Handler((_, _) => Task.FromResult(++count == 1 ? Response("secret response", HttpStatusCode.ServiceUnavailable) : Response(Hello)));
        using var client = new HttpClient(handler);
        using var budget = new LlmBudget();
        await Gateway(client, new()
        {
            Provider = "OpenAiCompatible",
            ApiKey = Secret,
            MaxRetries = 1
        }).CompleteAsync(Request, budget);
        Assert.Equal(2, handler.Calls);
        Assert.Equal(2, budget.Attempts);
        Assert.Equal(1, budget.ModelRounds);
        Assert.Equal(1000, budget.ReservedOutputTokens);
    }

    [Fact]
    public async Task RetryCannotExceedBudgetOrSendAnExtraRequest()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response("busy", HttpStatusCode.ServiceUnavailable)));
        using var client = new HttpClient(handler);
        using var budget = new LlmBudget(new(MaxOutputTokens: 500));
        var exception = await Assert.ThrowsAsync<LlmException>(() => Gateway(client,
            new()
            {
                Provider = "OpenAiCompatible",
                ApiKey = Secret,
                MaxRetries = 1
            }).CompleteAsync(Request, budget));
        Assert.Equal(LlmFailure.BudgetExceeded, exception.Failure);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task CallerCancellationReachesNetworkWithoutBeingReportedAsProviderTimeout()
    {
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler(async (_, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return Response(Hello);
        });
        using var client = new HttpClient(handler);
        using var budget = new LlmBudget();
        var call = Gateway(client).CompleteAsync(Request, budget, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task RequestTimeoutIsSanitizedAndDoesNotRetry()
    {
        using var handler = new Handler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Response(Hello);
        });
        using var client = new HttpClient(handler);
        using var budget = new LlmBudget();
        var exception = await Assert.ThrowsAsync<LlmException>(() => Gateway(client,
            new()
            {
                Provider = "OpenAiCompatible",
                ApiKey = Secret,
                RequestTimeoutSeconds = 1
            }).CompleteAsync(Request, budget));
        Assert.Equal(LlmFailure.Timeout, exception.Failure);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("http://public.example/v1")]
    [InlineData("https://user:password@example.com/v1")]
    [InlineData("https://example.com/v1?key=secret")]
    [InlineData("https://example.com/v1#secret")]
    [InlineData("https://example.com/v1/chat/completions")]
    [InlineData("file:///tmp/model")]
    public void RejectsUnsafeOrAmbiguousServerConfiguration(string endpoint)
    {
        var options = new AiOptions { Provider = "OpenAiCompatible", ApiKey = Secret, BaseUrl = endpoint };
        Assert.Equal(LlmFailure.InvalidConfiguration, Assert.Throws<LlmException>(() => options.ValidateEndpoint()).Failure);
    }

    [Fact]
    public async Task MissingKeyDoesNotContactNetworkOrBecomeFake()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Must not send."));
        using var client = new HttpClient(handler);
        using var budget = new LlmBudget();
        var exception = await Assert.ThrowsAsync<LlmException>(() => Gateway(client,
            new()
            {
                Provider = "OpenAiCompatible"
            }).CompleteAsync(Request, budget));
        Assert.Equal(LlmFailure.NotConfigured, exception.Failure);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("not json", LlmFailure.InvalidResponse)]
    [InlineData("{}", LlmFailure.InvalidResponse)]
    [InlineData("{\"choices\":[]}", LlmFailure.InvalidResponse)]
    [InlineData("{\"choices\":[{\"finish_reason\":\"length\"}]}", LlmFailure.Truncated)]
    [InlineData("{\"choices\":[{\"finish_reason\":\"content_filter\"}]}", LlmFailure.Refused)]
    [InlineData("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\",\"refusal\":\"cannot\"}}]}", LlmFailure.Refused)]
    public async Task MalformedRefusedAndTruncatedResponsesFailExplicitly(string response, LlmFailure failure)
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response(response)));
        using var client = new HttpClient(handler);
        using var budget = new LlmBudget();
        Assert.Equal(failure, (await Assert.ThrowsAsync<LlmException>(() => Gateway(client).CompleteAsync(Request, budget))).Failure);
    }

    [Fact]
    public async Task OversizedResponseIsRejected()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Response(new string('x', 2048))));
        using var client = new HttpClient(handler);
        using var budget = new LlmBudget();
        Assert.Equal(LlmFailure.InvalidResponse, (await Assert.ThrowsAsync<LlmException>(() => Gateway(client,
            new()
            {
                Provider = "OpenAiCompatible",
                ApiKey = Secret,
                MaxResponseBytes = 1024
            }).CompleteAsync(Request, budget))).Failure);
    }

    [Fact]
    public async Task UsageMissingIsUnknownAndReportedOverBudgetIsRejected()
    {
        var response = Hello.Replace(",\"usage\":{\"prompt_tokens\":20,\"completion_tokens\":3}", string.Empty, StringComparison.Ordinal);
        using var handler = new Handler((_, _) => Task.FromResult(Response(response)));
        using var client = new HttpClient(handler);
        using var budget = new LlmBudget();
        Assert.Null((await Gateway(client).CompleteAsync(Request, budget)).Usage);
        response = Hello.Replace("\"completion_tokens\":3", "\"completion_tokens\":501", StringComparison.Ordinal);
        Assert.Equal(LlmFailure.BudgetExceeded, (await Assert.ThrowsAsync<LlmException>(() => Gateway(client).CompleteAsync(Request, budget))).Failure);
    }

    [Fact]
    public async Task InvalidToolResultConversationCannotBeSent()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("Must not send."));
        using var client = new HttpClient(handler);
        using var budget = new LlmBudget();
        var request = new LlmRequest([new(LlmRole.Tool, "secret", ToolCallId: "unknown-call")]);
        Assert.Equal(LlmFailure.InvalidRequest, (await Assert.ThrowsAsync<LlmException>(() => Gateway(client).CompleteAsync(request, budget))).Failure);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task ModelCannotCallToolsThatWereNotSupplied()
    {
        const string response = """{"model":"model","choices":[{"finish_reason":"tool_calls","message":{"role":"assistant","content":null,"tool_calls":[{"id":"call_1","type":"function","function":{"name":"delete_asset","arguments":"{}"}}]}}]}""";
        using var handler = new Handler((_, _) => Task.FromResult(Response(response)));
        using var client = new HttpClient(handler);
        using var budget = new LlmBudget();
        Assert.Equal(LlmFailure.InvalidResponse, (await Assert.ThrowsAsync<LlmException>(() => Gateway(client).CompleteAsync(Request, budget))).Failure);
        Assert.Equal(0, budget.ToolCalls);
    }

    [Fact]
    public async Task DefaultRegistrationIsOfflineAndFakeCannotClaimRealVerification()
    {
        using var provider = new ServiceCollection().AddLogging().AddPlantOpsAi(new ConfigurationBuilder().Build()).BuildServiceProvider();
        Assert.IsType<FakeLlmGateway>(provider.GetRequiredService<ILlmGateway>());
        var report = await ProviderProbe.RunAsync(ProviderProbe.CreateFake(), new AiOptions(), true);
        Assert.True(report.ChecksPassed);
        Assert.True(report.Simulated);
        Assert.False(report.RealProviderVerified);
        Assert.Equal(4, report.Rounds);
        Assert.Equal(1, report.ToolCalls);
        Assert.Null(report.ReportedInputTokens);
        Assert.Null(report.EstimatedCost);
        Assert.Equal(4, report.Checks.Length);
        Assert.Equal(3, report.CaseBudgets.Length);
        Assert.All(report.CaseBudgets, item => Assert.InRange(item.ReservedInputTokens, 1, 16000));
    }

    [Fact]
    public async Task MissingModelCredentialDoesNotBreakBusinessLiveness()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AI:Provider"] = "OpenAiCompatible",
                ["AI:ApiKey"] = string.Empty,
                ["AI:BaseUrl"] = "https://unavailable.invalid/v1"
            }));
        });
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().GetAsync("/health/live")).StatusCode);
    }

    [Fact]
    public async Task HttpProtocolRoundTripPreservesToolResultsAndStrictInvestigationSchema()
    {
        var step = 0;
        using var handler = new Handler(async (request, ct) =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var body = json.RootElement;
            switch (step++)
            {
                case 0:
                    return Response(Hello);
                case 1:
                    Assert.Equal("get_asset_details", body.GetProperty("tool_choice").GetProperty("function").GetProperty("name").GetString());
                    var function = body.GetProperty("tools")[0].GetProperty("function");
                    Assert.True(function.GetProperty("strict").GetBoolean());
                    Assert.False(function.GetProperty("parameters").GetProperty("additionalProperties").GetBoolean());
                    Assert.False(body.GetProperty("parallel_tool_calls").GetBoolean());
                    return Response("""{"model":"gpt-6.1-sol","choices":[{"finish_reason":"tool_calls","message":{"role":"assistant","content":null,"tool_calls":[{"id":"call_1","type":"function","function":{"name":"get_asset_details","arguments":"{\"assetId\":\"demo-p101\"}"}}]}}],"usage":{"prompt_tokens":20,"completion_tokens":20}}""");
                case 2:
                    Assert.Equal("none", body.GetProperty("tool_choice").GetString());
                    var messages = body.GetProperty("messages");
                    Assert.Equal("call_1", messages[2].GetProperty("tool_calls")[0].GetProperty("id").GetString());
                    Assert.Equal("tool", messages[3].GetProperty("role").GetString());
                    Assert.Equal("call_1", messages[3].GetProperty("tool_call_id").GetString());
                    Assert.Contains("SyntheticProbe", messages[3].GetProperty("content").GetString());
                    return Response(Hello.Replace("Hello.", "E1", StringComparison.Ordinal));
                case 3:
                    var format = body.GetProperty("response_format");
                    Assert.Equal("json_schema", format.GetProperty("type").GetString());
                    Assert.True(format.GetProperty("json_schema").GetProperty("strict").GetBoolean());
                    Assert.Equal("investigation_v1", format.GetProperty("json_schema").GetProperty("name").GetString());
                    var content = InvestigationContract.Serialize(new("1", "Synthetic protocol check.",
                        [new(ProviderProbe.ProbeObservation, ["E1"])], [], [], ["No field data."], [], ["E1"]));
                    return Response(JsonSerializer.Serialize(new
                    {
                        model = "gpt-6.1-sol",
                        choices = new[] { new { finish_reason = "stop", message = new { role = "assistant", content } } },
                        usage = new
                        {
                            prompt_tokens = 20,
                            completion_tokens = 100
                        }
                    }));
                default:
                    throw new InvalidOperationException("Unexpected extra call.");
            }
        });
        using var client = new HttpClient(handler);
        var options = new AiOptions { Provider = "OpenAiCompatible", ApiKey = Secret, MaxRetries = 0 };
        var report = await ProviderProbe.RunAsync(Gateway(client, options), options, false);
        Assert.True(report.ChecksPassed);
        Assert.Equal(4, handler.Calls);
        Assert.Equal(1, report.ToolCalls);
        Assert.Equal(80, report.ReportedInputTokens);
        Assert.Equal(126, report.ReportedOutputTokens);
        Assert.Null(report.EstimatedCost);
    }

    private static OpenAiCompatibleGateway Gateway(HttpClient client, AiOptions? options = null) =>
        new(client, Options.Create(options ?? new()
        {
            Provider = "OpenAiCompatible",
            ApiKey = Secret,
            MaxRetries = 0
        }));

    private static HttpResponseMessage Response(string text, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
        {
            Content = new StringContent(text, Encoding.UTF8, "application/json")
        };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls
        {
            get; private set;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return send(request, cancellationToken);
        }
    }
}
