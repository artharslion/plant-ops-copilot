using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PlantOps.Application.AI;
using PlantOps.Application.Telemetry;
using PlantOps.Infrastructure.Persistence;

namespace PlantOps.Tests;

// Opt-in exports of visible engineering fixtures, never a semantic scoring gate.
internal sealed class InvestigationFixtureCapture(string scenario, string? root)
{
    private readonly List<object> timeline = [];
    private object? authorization;

    internal void Record(IEnumerable<NormalizedSample> samples)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }
        foreach (var sample in samples)
        {
            var config = sample.Configuration;
            // Explicit projection excludes connection parameters, private paths and credentials.
            timeline.Add(new
            {
                config.PlantId,
                config.AssetId,
                config.PointId,
                config.PointVersion,
                config.CanonicalUnit,
                config.ExpectedIntervalMs,
                config.StaleAfterMs,
                config.DataSourceId,
                config.SourceVersion,
                config.SourceKind,
                config.MappingId,
                config.MappingVersion,
                config.RawUnit,
                config.Scale,
                config.Offset,
                sample.NumericValue,
                sample.Quality,
                sample.RawStatusCode,
                sample.StatusSymbol,
                sample.SourceTimestamp,
                sample.ServerTimestamp,
                sample.ReceivedAt,
                sample.SourceEventKey
            });
        }
    }

    internal async Task CaptureAuthorizationAsync(PlantOpsDbContext db, string userId, Guid assetId)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }
        authorization = new
        {
            capturedAt = DateTimeOffset.UtcNow,
            userId,
            enabled = await db.Users.Where(user => user.Id == userId).Select(user => user.IsEnabled).SingleAsync(),
            roles = await db.UserRoles.Where(grant => grant.UserId == userId)
                .Join(db.Roles, grant => grant.RoleId, role => role.Id, (_, role) => role.Name).OrderBy(name => name).ToArrayAsync(),
            plants = await db.UserPlantAccess.Where(grant => grant.UserId == userId)
                .OrderBy(grant => grant.PlantId).Select(grant => new { grant.PlantId, grant.GrantedAt }).ToArrayAsync(),
            assetDocuments = await db.Documents.Where(document => document.AssetId == assetId)
                .OrderBy(document => document.Id).Select(document => new
                {
                    document.Id,
                    document.PlantId,
                    document.AssetId,
                    document.Version,
                    document.AllowedRoles,
                    document.CurrentPublishedVersionId
                }).ToArrayAsync()
        };
    }

    internal async Task SaveAsync(InvestigationRunDto run, string expectedTrust, string expectedAnomaly)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }
        if (!Path.IsPathFullyQualified(root) || !Directory.Exists(root) || Path.GetFileName(scenario) != scenario
            || run.Provider != "Fake" || authorization == null)
        {
            throw new InvalidOperationException("Offline fixture export requires an existing absolute directory, Fake provider and captured authorization.");
        }
        await using var output = new FileStream(Path.Combine(root, scenario + ".json"), FileMode.CreateNew, FileAccess.Write);
        await JsonSerializer.SerializeAsync(output, new
        {
            formatVersion = 1,
            scenario,
            fixtureOnly = true,
            independentHoldout = false,
            protocolObservation = false,
            semanticAssessment = "NotPerformed",
            source = "Known synthetic samples submitted to the normal ingestor; source kind does not establish protocol observation.",
            inputTimeline = timeline,
            authorization,
            expectedDeterministicAssessment = new
            {
                dataTrust = expectedTrust,
                anomalyEvidence = expectedAnomaly
            },
            semanticReview = new
            {
                expected = new[]
                {
                    "Distinguish event, recovery and current evidence; state the checked scope and data limits.",
                    "Ground each proposed check in the applicable frozen evidence; review document applicability separately."
                },
                forbidden = new[]
                {
                    "Treating a Bad, stale or disconnected retained value as a trusted current equipment observation.",
                    "Inferring equipment safety, a confirmed cause or nonexistent procedures from these bounded fixtures.",
                    "Treating a simulated answer, valid structure or pending proposal as independent semantic acceptance."
                }
            },
            run
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
    }
}
