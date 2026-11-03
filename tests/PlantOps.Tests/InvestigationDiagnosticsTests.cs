using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PlantOps.AI;
using PlantOps.Application.AI;
using PlantOps.Infrastructure.AI;
using Xunit;

namespace PlantOps.Tests;

public sealed class InvestigationDiagnosticsTests
{
    [Theory]
    [InlineData(false, "no-observational-support")]
    [InlineData(true, "overlapping-support-opposition")]
    public async Task RejectedHypothesisRetainsExactPrivateAnswerAndSafeRuleDetails(bool overlap, string branch)
    {
        var point = Guid.NewGuid();
        InvestigationEvidence[] evidence =
        [
            new("E1", "PointStatus", "Vibration", ["Current anomaly."], "h", DateTimeOffset.UtcNow, point,
                PointStatus: new() { PointId = point, CurrentTrust = "Trusted", CurrentAnomaly = "Present" }),
            new("D1", "Document", "Manual", ["Inspection procedure."], "h", DateTimeOffset.UtcNow, Guid.NewGuid())
        ];
        var advice = InvestigationAdviceTests.Valid(point.ToString(), "E1");
        advice = advice with
        {
            Hypotheses = [new("H-private", "PRIVATE_REJECTED_TEXT", "Weak", overlap ? ["E1"] : ["D1"], overlap ? ["E1"] : [], ["R1"])]
        };
        var raw = InvestigationAdviceContract.Serialize(advice);
        using var budget = new LlmBudget(new(MaxInputTokens: 32000, MaxOutputTokens: 6000));
        string? captured = null;
        budget.ResponseObserver = (_, _, _, completion, _) =>
        {
            captured = completion.Content;
            return Task.FromResult(new InvestigationResponseReceipt("Stored", "private-id"));
        };
        var engine = new InvestigationEngine(new ScriptedLlmGateway([new(raw, [], "fake", new(100, 40))]), Options.Create(new AiOptions()));
        var exception = await Assert.ThrowsAsync<LlmException>(() => engine.ExecuteAsync(evidence,
            (_, _, _) => throw new InvalidOperationException(), _ => Task.CompletedTask, budget, CancellationToken.None));
        Assert.Equal("advice-hypothesis-basis", exception.ValidationRule);
        Assert.Equal(branch, exception.ValidationDetails!.Branch);
        Assert.Equal("$.hypotheses[0]", exception.ValidationDetails.Path);
        Assert.Equal(overlap ? ["E1"] : new[] { "D1" }, exception.ValidationDetails.EvidenceRefs);
        Assert.Equal(raw, captured);
        var diagnostics = JsonSerializer.Serialize(budget.Diagnostics);
        Assert.Contains(branch, diagnostics);
        Assert.DoesNotContain("PRIVATE_REJECTED_TEXT", diagnostics);
        Assert.DoesNotContain("H-private", diagnostics);
        Assert.DoesNotContain("PRIVATE_REJECTED_TEXT", exception.ToString());
    }

    [Fact]
    public async Task CaptureIsDeduplicatedAndStorageFailureDoesNotChangeTheAnswer()
    {
        using var budget = new LlmBudget();
        var calls = 0;
        budget.ResponseObserver = (_, _, _, _, _) =>
        {
            calls++;
            throw new IOException("PRIVATE_STORAGE_PATH");
        };
        var completion = new LlmCompletion("PRIVATE_RESPONSE", [], "fake", null);
        await budget.RecordResponseAsync(completion, "hash", CancellationToken.None);
        await budget.RecordResponseAsync(completion, "hash", CancellationToken.None);
        Assert.Equal(1, calls);
        var diagnostic = Assert.Single(budget.Diagnostics);
        Assert.Equal("WriteFailed", diagnostic.GetProperty("details").GetProperty("captureStatus").GetString());
        Assert.DoesNotContain("PRIVATE_", diagnostic.GetRawText());
    }

    [Theory]
    [InlineData("success", "Completed")]
    [InlineData("malformed", "Rejected")]
    [InlineData("truncated", "Rejected")]
    [InlineData("unavailable", "Rejected")]
    [InlineData("transport", "TransportFailed")]
    [InlineData("stream-valid", "Validated")]
    [InlineData("stream-invalid", "Invalid")]
    public async Task GatewayPersistsFailureStageWithoutCredentialsOrResponseText(string scenario, string expectedOutcome)
    {
        using var handler = new DiagnosticHandler(scenario);
        using var client = new HttpClient(handler);
        using var budget = new LlmBudget();
        var captured = 0;
        budget.ResponseObserver = (_, _, _, _, _) =>
        {
            captured++;
            return Task.FromResult(new InvestigationResponseReceipt("Stored"));
        };
        var gateway = new OpenAiCompatibleGateway(client, Options.Create(new AiOptions
        {
            Provider = "OpenAiCompatible",
            BaseUrl = "https://example.invalid/v1",
            ApiKey = "PRIVATE_API_KEY",
            MaxRetries = 0,
            StreamResponses = scenario.StartsWith("stream-", StringComparison.Ordinal)
        }));
        var request = new LlmRequest([new(LlmRole.User, "PRIVATE_REQUEST")]);
        if (scenario is "success" or "stream-valid")
        {
            await gateway.CompleteAsync(request, budget);
        }
        else
        {
            await Assert.ThrowsAsync<LlmException>(() => gateway.CompleteAsync(request, budget));
        }
        Assert.Contains(budget.Diagnostics, x => x.GetProperty("outcome").GetString() == expectedOutcome);
        Assert.Equal(scenario is "success" or "stream-valid" ? 1 : 0, captured);
        var metadata = JsonSerializer.Serialize(budget.Diagnostics);
        Assert.DoesNotContain("PRIVATE_", metadata);
        Assert.DoesNotContain("example.invalid", metadata);
    }

    [Fact]
    public async Task ParsedAnswerIsCapturedEvenWhenReportedUsageExceedsBudget()
    {
        using var handler = new DiagnosticHandler("over-budget");
        using var client = new HttpClient(handler);
        using var budget = new LlmBudget();
        var captured = false;
        budget.ResponseObserver = (_, _, _, completion, _) =>
        {
            captured = completion.Content == "PRIVATE_RESPONSE";
            return Task.FromResult(new InvestigationResponseReceipt("Stored"));
        };
        var gateway = new OpenAiCompatibleGateway(client, Options.Create(new AiOptions
        {
            Provider = "OpenAiCompatible",
            ApiKey = "PRIVATE_KEY",
            MaxRetries = 0
        }));
        var exception = await Assert.ThrowsAsync<LlmException>(() => gateway.CompleteAsync(new([new(LlmRole.User, "Hello")]), budget));
        Assert.Equal(LlmFailure.BudgetExceeded, exception.Failure);
        Assert.True(captured);
        Assert.Contains(budget.Diagnostics, x => x.GetProperty("stage").GetString() == "usage-reconciliation");
    }

    [Fact]
    public void DiagnosticTimelineIsBounded()
    {
        using var budget = new LlmBudget();
        for (var i = 0; i < 140; i++)
        {
            budget.RecordDiagnostic("test", "Received");
        }
        Assert.Equal(128, budget.Diagnostics.Count);
        Assert.Equal(12, budget.OmittedDiagnosticCount);
    }

    [Fact]
    public async Task PrivateStoreRoundTripsAndPrunesOnlyItsOwnExpiredFilesEvenWhenDisabled()
    {
        using var folder = new DiagnosticFolder();
        var configuration = folder.Configuration();
        using var store = new InvestigationResponseStore(configuration, TimeProvider.System, NullLogger<InvestigationResponseStore>.Instance);
        var capture = new InvestigationResponseCapture(Guid.NewGuid(), Guid.NewGuid(), 2, 2, "request-hash", DateTimeOffset.UtcNow,
            new("PRIVATE_EXACT_回答", [new("call", "get_asset_details", "{}")], "fixture", new(100, 20)));
        var receipt = await store.SaveAsync(capture, CancellationToken.None);
        Assert.Equal("Stored", receipt.Status);
        var file = Path.Combine(folder.Root, receipt.CaptureId!);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(file));
        Assert.Equal(capture.RunId, document.RootElement.GetProperty("capture").GetProperty("runId").GetGuid());
        Assert.Equal(capture.Completion.Content, document.RootElement.GetProperty("capture").GetProperty("completion").GetProperty("content").GetString());
        Assert.False(Path.IsPathFullyQualified(receipt.CaptureId!));
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-8));
        var unrelated = Path.Combine(folder.Root, "unrelated.txt");
        await File.WriteAllTextAsync(unrelated, "keep");
        File.SetLastWriteTimeUtc(unrelated, DateTime.UtcNow.AddDays(-8));
        configuration["AI:Diagnostics:CaptureResponses"] = "false";
        Assert.Equal("Disabled", (await store.SaveAsync(capture, CancellationToken.None)).Status);
        await store.PruneAsync(CancellationToken.None);
        Assert.False(File.Exists(file));
        Assert.True(File.Exists(unrelated));
    }

    [Theory]
    [InlineData(false, "SizeLimit")]
    [InlineData(true, "WriteFailed")]
    public async Task PrivateStoreReportsOversizeAndUnwritableStorage(bool unwritable, string status)
    {
        using var folder = new DiagnosticFolder();
        var configuration = folder.Configuration();
        configuration["AI:Diagnostics:MaxCaptureBytes"] = "1024";
        if (unwritable)
        {
            var blocked = Path.Combine(folder.Root, "blocked");
            await File.WriteAllTextAsync(blocked, "file, not a directory");
            configuration["AI:Diagnostics:StorageRoot"] = blocked;
        }
        using var store = new InvestigationResponseStore(configuration, TimeProvider.System, NullLogger<InvestigationResponseStore>.Instance);
        var receipt = await store.SaveAsync(new(Guid.NewGuid(), Guid.NewGuid(), 1, 1, "hash", DateTimeOffset.UtcNow,
            new(unwritable ? "answer" : new string('x', 2000), [], "fixture", null)), CancellationToken.None);
        Assert.Equal(status, receipt.Status);
        Assert.Empty(Directory.GetFiles(folder.Root, "response-*.json"));
    }

    [Fact]
    public async Task TotalStorageLimitEvictsTheOldestCapture()
    {
        using var folder = new DiagnosticFolder();
        var configuration = folder.Configuration();
        configuration["AI:Diagnostics:MaxCaptureBytes"] = "1048576";
        configuration["AI:Diagnostics:MaxTotalBytes"] = "1048576";
        using var store = new InvestigationResponseStore(configuration, TimeProvider.System, NullLogger<InvestigationResponseStore>.Instance);
        var capture = new InvestigationResponseCapture(Guid.NewGuid(), Guid.NewGuid(), 1, 1, "hash", DateTimeOffset.UtcNow,
            new(new string('x', 600000), [], "fixture", null));
        var first = await store.SaveAsync(capture, CancellationToken.None);
        File.SetLastWriteTimeUtc(Path.Combine(folder.Root, first.CaptureId!), DateTime.UtcNow.AddMinutes(-5));
        var second = await store.SaveAsync(capture, CancellationToken.None);
        Assert.False(File.Exists(Path.Combine(folder.Root, first.CaptureId!)));
        Assert.True(File.Exists(Path.Combine(folder.Root, second.CaptureId!)));
        Assert.True(Directory.GetFiles(folder.Root).Sum(x => new FileInfo(x).Length) <= 1048576);
    }

    [Fact]
    public async Task InvalidDiagnosticConfigurationDoesNotBlockInvestigationQueueMaintenance()
    {
        using var folder = new DiagnosticFolder();
        var configuration = folder.Configuration();
        configuration["AI:Diagnostics:RetentionDays"] = "invalid";
        configuration["AI:Diagnostics:MaxCaptureBytes"] = "invalid";
        using var store = new InvestigationResponseStore(configuration, TimeProvider.System, NullLogger<InvestigationResponseStore>.Instance);
        await store.PruneAsync(CancellationToken.None);
        Assert.Equal("WriteFailed", (await store.SaveAsync(new(Guid.NewGuid(), Guid.NewGuid(), 1, 1, "hash", DateTimeOffset.UtcNow,
            new("answer", [], "fixture", null)), CancellationToken.None)).Status);
    }

    private sealed class DiagnosticHandler(string scenario) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (scenario == "transport")
            {
                throw new HttpRequestException("PRIVATE_TRANSPORT_ERROR");
            }
            if (scenario.StartsWith("stream-", StringComparison.Ordinal))
            {
                var sse = scenario == "stream-invalid" ? "data: PRIVATE_INVALID_SSE\n\n" :
                    "data: {\"model\":\"fixture\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"PRIVATE_RESPONSE\"},\"finish_reason\":null}]}\n\n"
                    + "data: {\"model\":\"fixture\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n"
                    + "data: {\"model\":\"fixture\",\"choices\":[],\"usage\":{\"prompt_tokens\":20,\"completion_tokens\":3}}\n\n"
                    + "data: [DONE]\n\n";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") });
            }
            var finish = scenario == "truncated" ? "length" : "stop";
            var input = scenario == "over-budget" ? 40000 : 20;
            var json = scenario == "malformed" ? "PRIVATE_INVALID_JSON" : JsonSerializer.Serialize(new
            {
                model = "fixture",
                choices = new[] { new { finish_reason = finish, message = new { role = "assistant", content = "PRIVATE_RESPONSE" } } },
                usage = new
                {
                    prompt_tokens = input,
                    completion_tokens = 3
                }
            });
            return Task.FromResult(new HttpResponseMessage(scenario == "unavailable" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class DiagnosticFolder : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "plantops-diagnostics-" + Guid.NewGuid().ToString("N"));

        public DiagnosticFolder() => Directory.CreateDirectory(Root);

        public IConfigurationRoot Configuration() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AI:Diagnostics:StorageRoot"] = Root
        }).Build();

        public void Dispose()
        {
            // Flat test-owned directory: no recursive deletion or computed parent paths.
            foreach (var file in Directory.GetFiles(Root))
            {
                File.Delete(file);
            }
            Directory.Delete(Root);
        }
    }
}
