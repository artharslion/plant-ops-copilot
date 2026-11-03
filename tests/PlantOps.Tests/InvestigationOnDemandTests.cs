using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PlantOps.AI;
using PlantOps.Application.AI;
using PlantOps.Application.Assets;
using PlantOps.Application.Knowledge;
using PlantOps.Application.Telemetry;
using Xunit;

namespace PlantOps.Tests;

[Collection("Database")]
// Scripted read decisions plus demo-generated final payloads verify plumbing only.
public sealed class InvestigationOnDemandTests
{
    [DatabaseFact]
    public async Task SufficientInitialEvidenceFinishesWithoutReadingOptionalTools()
    {
        var gateway = new DemandGateway([]);
        var run = await RunAsync(gateway);
        Assert.Equal("Succeeded", run.Status);
        Assert.Equal(1, run.ToolCalls);
        Assert.Equal(1, run.ModelRounds);
        Assert.DoesNotContain(run.Evidence, x => x.Kind is "EventTrend" or "Maintenance" or "Document" or "AlarmHistory");
        Assert.Contains(run.Evidence, x => x.Kind == "PointStatus");
        Assert.All(run.Evidence.Where(x => x.PointStatus != null), item =>
        {
            Assert.Equal(item.PointStatus!.CurrentTrust, item.State!.CurrentTrust);
            Assert.Equal(item.PointStatus.CurrentAnomaly, item.State.CurrentAnomaly);
        });
        Assert.Equal("3", run.Result!.Value.GetProperty("schemaVersion").GetString());
        Assert.NotEmpty(run.Warnings);
        Assert.Single(run.RunRecord!.Value.GetProperty("toolTrace").EnumerateArray());
        Assert.Equal("get_asset_details", run.RunRecord.Value.GetProperty("toolTrace")[0].GetProperty("name").GetString());
    }

    [DatabaseFact]
    public async Task RepeatedMeasurementRequestReusesFrozenEvidenceAndRetainsLateWarnings()
    {
        var gateway = new DemandGateway([new("first", "get_recent_measurements", "{}"), new("again", "get_recent_measurements", "{}")]);
        var run = await RunAsync(gateway, addLateReading: true);
        Assert.True(run.Status == "Succeeded", run.Status + ":" + run.FailureCode + ":" + run.RunRecord?.GetRawText());
        Assert.Equal(3, run.ToolCalls);
        Assert.Equal(2, run.ModelRounds);
        Assert.Contains(run.Evidence, x => x.Kind == "EventTrend");
        Assert.DoesNotContain(run.Evidence, x => x.Kind is "Maintenance" or "Document" or "AlarmHistory");
        var trace = run.RunRecord!.Value.GetProperty("toolTrace").EnumerateArray().ToArray();
        Assert.Equal(3, trace.Length);
        Assert.False(trace[1].GetProperty("cacheHit").GetBoolean());
        Assert.True(trace[2].GetProperty("cacheHit").GetBoolean());
        Assert.Equal(trace[1].GetProperty("key").GetString(), trace[2].GetProperty("key").GetString());
        Assert.Equal(trace[1].GetProperty("evidence").GetRawText(), trace[2].GetProperty("evidence").GetRawText());
        Assert.All(run.Evidence.Where(x => x.DerivedTrend != null), x =>
            Assert.Equal(run.RunRecord.Value.GetProperty("asOf").GetDateTimeOffset(), x.DerivedTrend!.AsOf));
        Assert.All(run.Evidence.Where(x => x.DerivedTrend != null).SelectMany(x => x.DerivedTrend!.Samples), sample =>
        {
            Assert.True(sample.ReceivedAt <= run.RunRecord!.Value.GetProperty("asOf").GetDateTimeOffset());
            Assert.NotEqual(777, sample.Value);
        });
        Assert.All(run.Warnings, warning => Assert.Contains(run.Evidence, e => e.Kind == "DataQuality" && e.Facts.Contains(warning)));
        Assert.Contains(run.Warnings, x => x.Contains("实际查询水位", StringComparison.Ordinal));
        Assert.Contains("实际查询水位", gateway.LastInput!, StringComparison.Ordinal);
        Assert.Equal("investigation_v3", gateway.FirstSchema);
        Assert.Equal("investigation_v3", gateway.LastSchema);
        Assert.NotEmpty(Assert.Single(run.Assessment!.Points).Windows);
    }

    [DatabaseFact]
    public async Task FailedDocumentReadReturnsSanitizedEvidenceAndCachedFailure()
    {
        var gateway = new DemandGateway([
            new("first", "search_documents", "{\"query\":\"temperature\",\"limit\":1}"),
            new("again", "search_documents", "{\"limit\":1,\"query\":\"temperature\"}")]);
        var documents = new UnavailableDocuments();
        var run = await RunAsync(gateway, documents);
        Assert.True(run.Status == "Succeeded", run.Status + ":" + run.FailureCode + ":" + run.RunRecord?.GetRawText());
        Assert.Equal(1, documents.Calls);
        Assert.Equal(3, run.ToolCalls);
        Assert.Contains(run.Warnings, x => x.Contains("search_documents", StringComparison.Ordinal) && x.Contains("不可用", StringComparison.Ordinal));
        Assert.DoesNotContain("private-error", JsonSerializer.Serialize(run), StringComparison.Ordinal);
        Assert.Contains("不可用", gateway.LastInput!, StringComparison.Ordinal);
        var trace = run.RunRecord!.Value.GetProperty("toolTrace");
        Assert.Equal("Unavailable", trace[1].GetProperty("outcome").GetString());
        Assert.Contains(run.Evidence, x => x.Kind == "DataQuality" && x.State?.Availability == "Unavailable");
        Assert.True(trace[2].GetProperty("cacheHit").GetBoolean());
        Assert.Equal(trace[1].GetProperty("key").GetString(), trace[2].GetProperty("key").GetString());
        Assert.All(run.Warnings, warning => Assert.Contains(run.Evidence, e => e.Kind == "DataQuality" && e.Facts.Contains(warning)));
    }

    private static async Task<InvestigationRunDto> RunAsync(DemandGateway gateway, IDocumentEvidenceService? documents = null, bool addLateReading = false)
    {
        var clock = new AlarmTests.Clock();
        await using var session = new AssetManagementTests.Session(clock, await TestDatabase.IsolatedConnection(), configureServices: services =>
        {
            services.AddSingleton<ILlmGateway>(gateway);
            if (documents != null)
            {
                services.AddSingleton(documents);
            }
        });
        await session.Initialize();
        await session.Login();
        var fixture = await AlarmTests.Configure(session);
        var alarm = await AlarmTests.Raise(session, fixture, clock);
        if (addLateReading)
        {
            gateway.BeforeTools = async () =>
            {
                var observedAt = clock.Now.AddMilliseconds(-500);
                clock.Now = clock.Now.AddSeconds(1);
                await using var ingestion = session.Factory.Services.CreateAsyncScope();
                await ingestion.ServiceProvider.GetRequiredService<IMeasurementIngestor>().IngestAsync([
                    SamplePolicy.Normalize(fixture.Config, 777, "Good", observedAt, null, clock.Now, "after-asof")]);
            };
        }
        var created = await session.Client.PostAsync($"/api/v1/alarms/{alarm.Id}/investigations", null);
        var run = (await created.Content.ReadFromJsonAsync<InvestigationRunDto>())!;
        await using var scope = session.Factory.Services.CreateAsyncScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<IInvestigationService>().ProcessNextAsync());
        return (await session.Client.GetFromJsonAsync<InvestigationRunDto>($"/api/v1/investigations/{run.Id}"))!;
    }

    private sealed class DemandGateway(LlmToolCall[] calls) : ILlmGateway
    {
        private int round;

        public Func<Task>? BeforeTools
        {
            get; set;
        }

        public string? FirstSchema
        {
            get; private set;
        }

        public string? LastSchema
        {
            get; private set;
        }

        public string? LastInput
        {
            get; private set;
        }

        public async Task<LlmCompletion> CompleteAsync(LlmRequest request, LlmBudget budget, CancellationToken cancellationToken = default)
        {
            FirstSchema ??= request.OutputSchema!.Name;
            LastSchema = request.OutputSchema!.Name;
            LastInput = request.Messages.First(x => x.Role == LlmRole.User).Content;
            if (round++ == 0 && calls.Length > 0)
            {
                if (BeforeTools != null)
                {
                    await BeforeTools();
                }
                using var input = JsonDocument.Parse(LastInput!);
                Assert.DoesNotContain(input.RootElement.GetProperty("evidence").EnumerateArray(), x =>
                    x.GetProperty("kind").GetString() is "EventTrend" or "Maintenance" or "Document" or "AlarmHistory");
                return await new ScriptedLlmGateway([new(null, calls, "fake-demand", new(100, 20))]).CompleteAsync(request, budget, cancellationToken);
            }
            // Produce a grounded answer without adding any implicit tool requests.
            return await new FakeLlmGateway().CompleteAsync(request with
            {
                DisableToolCalls = true
            }, budget, cancellationToken);
        }
    }

    private sealed class UnavailableDocuments : IDocumentEvidenceService
    {
        public int Calls
        {
            get; private set;
        }

        public Task<DocumentSearchResult> SearchAsync(Actor actor, Guid assetId, string query, int limit = 6, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new IOException("private-error: credential material must never leave the tool");
        }

        public Task<DocumentEvidence> ExpandAsync(Actor actor, Guid assetId, Guid documentId, Guid versionId, Guid chunkId, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }
}
