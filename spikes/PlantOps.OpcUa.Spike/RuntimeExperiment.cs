extern alias worker;

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.FileProviders;
using Npgsql;
using PlantOps.Application.Telemetry;
using PlantOps.Domain.Telemetry;
using PlantOps.Domain.Alarms;
using PlantOps.Infrastructure;
using PlantOps.Infrastructure.Identity;
using PlantOps.Infrastructure.Persistence;
using PlantOps.Integrations.OpcUa;
using MeasurementWorker = worker::PlantOps.Worker.MeasurementWorker;

namespace PlantOps.OpcUa.Spike;

internal static class RuntimeExperiment
{
    private sealed class RuntimeEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";

        public string ApplicationName { get; set; } = "PlantOps.OpcUa.Spike";

        public string ContentRootPath { get; set; } = root;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    public static async Task RunAsync(string root, bool minimalScenario = false, string? industrialSimRoot = null, bool briefLow = false,
        bool disconnected = false)
    {
        var connection = Environment.GetEnvironmentVariable("PLANTOPS_RUNTIME_CONNECTION") ?? "";
        var parsed = new NpgsqlConnectionStringBuilder(connection);
        var parent = Path.GetFullPath("artifacts/opcua-runtime") + Path.DirectorySeparatorChar;
        if (parsed.Database != "plantops_tests" || !Regex.IsMatch(parsed.SearchPath ?? "", "^opcua_runtime_[a-z0-9_]{1,40}$")
            || !root.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || Directory.EnumerateFileSystemEntries(root).Any())
        {
            throw new ArgumentException("Use plantops_tests, one NEW opcua_runtime_* schema and empty artifacts/opcua-runtime/<run> directory.");
        }
        var disk = new DriveInfo(Path.GetPathRoot(root)!);
        var initialFree = disk.AvailableFreeSpace;
        if (initialFree < 2L * 1024 * 1024 * 1024)
        {
            throw new InvalidOperationException("Runtime experiment requires 2 GiB free before setup.");
        }
        var liveHeating = minimalScenario && industrialSimRoot == null && MinimalScenario.LiveHeating;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(liveHeating ? 900 : disconnected ? 480 : 150));
        var ct = deadline.Token;
        var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:PlantOps"] = connection,
            ["Demo:Password"] = Environment.GetEnvironmentVariable("PLANTOPS_RUNTIME_DEMO_PASSWORD")
                ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(24)) + "a!",
            ["Demo:SeedHistory"] = "false",
            ["Documents:StorageRoot"] = Path.Combine(root, "documents"),
            ["Telemetry:SimulationEnabled"] = "false"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<IConfiguration>(settings);
        services.AddSingleton<IHostEnvironment>(new RuntimeEnvironment(root));
        services.AddPlantOpsInfrastructure(settings);
        var industrial = industrialSimRoot != null;
        var approved = new OpcUaApprovedRuntimeEndpoint(industrial ? IndustrialSimProcess.Endpoint : Program.Endpoint,
            industrial ? "urn:industrial-sim:server" : "urn:localhost:plantops:spike:server",
            industrial ? IndustrialSimProcess.NamespaceUri : Program.NamespaceUri,
            industrial ? IndustrialSimProcess.Nodes.Select(node => "String:" + node).ToArray() :
            minimalScenario ? MinimalScenario.Points.Select(point => "String:" + point.Identifier).ToArray()
                : ["String:North.Utilities.P101.VibrationRms", "String:North.Point001.VibrationRms", "String:North.Point002.VibrationRms"],
            Path.Combine(root, "client-certificates"));
        services.AddSingleton<IOpcUaConnector>(new OpcUaRuntimeConnector(_ => approved, true, true));
        services.AddSingleton<IMeasurementSource, OpcUaRuntimeSource>();
        services.AddSingleton<MeasurementWorker>();
        await using var provider = services.BuildServiceProvider();
        Guid sourceId;
        Guid[] pointIds;
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
            // The schema identifier is restricted by the anchored allowlist above; reused schemas fail.
            var createSchema = $"CREATE SCHEMA \"{parsed.SearchPath}\"";
            await db.Database.ExecuteSqlRawAsync(createSchema, ct);
            await DevelopmentIdentityInitializer.InitializeAsync(scope.ServiceProvider, settings, "Development");
            await db.SourceMappings.ExecuteUpdateAsync(update => update.SetProperty(mapping => mapping.Enabled, false), ct);
            await db.DataSources.ExecuteUpdateAsync(update => update.SetProperty(source => source.Enabled, false), ct);
            var point = await db.MeasurementPoints.SingleAsync(point => point.AssetId == Guid.Parse("30000000-0000-0000-0000-000000000001")
                && point.Code == "VIBRATION_RMS", ct);
            var points = new[]
            {
                point, new MeasurementPoint
            {
                Id = Guid.NewGuid(), PlantId = point.PlantId, AssetId = point.AssetId,
                Code = "OPC_SECOND", Name = "Second protocol observation"
            }, new MeasurementPoint
                {
                    Id = Guid.NewGuid(),
                PlantId = point.PlantId, AssetId = point.AssetId, Code = "OPC_THIRD", Name = "Third protocol observation"
                }
            };
            if (minimalScenario)
            {
                points = MinimalScenario.Points.Select(definition => new MeasurementPoint
                {
                    Id = Guid.NewGuid(),
                    PlantId = point.PlantId,
                    AssetId = point.AssetId,
                    Code = definition.Code,
                    Name = definition.Identifier,
                    Quantity = definition.Quantity,
                    CanonicalUnit = definition.Unit
                }).ToArray();
                db.MeasurementPoints.AddRange(points);
                await db.AlarmRules.ExecuteUpdateAsync(update => update.SetProperty(rule => rule.Enabled, false), ct);
                db.AlarmRules.Add(new AlarmRule
                {
                    Id = Guid.NewGuid(),
                    PlantId = point.PlantId,
                    PointId = points[0].Id,
                    Name = "Fictional temperature demo, not maintenance guidance",
                    RaiseAbove = 80,
                    ClearBelow = 75,
                    RaiseForMs = 3000,
                    ClearForMs = 5000
                });
            }
            else
            {
                db.MeasurementPoints.AddRange(points.Skip(1));
            }
            var source = new DataSource
            {
                Id = Guid.NewGuid(),
                PlantId = point.PlantId,
                Name = "Controlled real protocol / simulated observations",
                Kind = "OpcUa",
                Enabled = true,
                ConnectionParameters = JsonSerializer.Serialize(new OpcUaConnectionInput("runtime-spike"))
            };
            sourceId = source.Id;
            pointIds = points.Select(point => point.Id).ToArray();
            db.DataSources.Add(source);
            for (var index = 0; index < points.Length; index++)
            {
                var mapping = new OpcUaMappingInput(approved.NamespaceUri, "String", approved.Nodes[index]["String:".Length..]);
                db.SourceMappings.Add(new SourceMapping
                {
                    Id = Guid.NewGuid(),
                    PlantId = point.PlantId,
                    PointId = points[index].Id,
                    DataSourceId = sourceId,
                    SourceKey = OpcUaConfigurationPolicy.Key(mapping),
                    RawUnit = points[index].CanonicalUnit,
                    SourceParameters = JsonSerializer.Serialize(mapping),
                    Enabled = true
                });
            }
            await db.SaveChangesAsync(ct);
        }
        var telemetry = new SpikeTelemetry();
        var application = industrial ? null : await Program.ConfigureAsync("Server", Path.Combine(root, "server"), telemetry, localRuntime: true);
        var scenario = minimalScenario ? new MinimalScenario() : null;
        var server = industrial ? null : new SimulatorServer(new(Constant: false), scenario);
        string? scenarioPath = null;
        if (briefLow)
        {
            scenarioPath = Path.Combine(root, "brief-low.yaml");
            var baseline = await File.ReadAllTextAsync(Path.Combine(industrialSimRoot!, "examples/scenarios/plantops-three-point.yaml"), ct);
            var pulse = baseline[..baseline.LastIndexOf("    - at:", StringComparison.Ordinal)]
                .Replace("plantops-three-point-v1", "plantops-brief-low-v1", StringComparison.Ordinal);
            await File.WriteAllTextAsync(scenarioPath, pulse + """
                    - at: 58s
                      set: { device: plantops-p101, datapoint: temperature, value: 60 }
                    - at: 61s
                      set: { device: plantops-p101, datapoint: temperature, value: 90 }
                """ + "\n", ct);
        }
        await using var external = industrial ? new IndustrialSimProcess(industrialSimRoot!, Path.Combine(root, "industrial-sim"), scenarioPath) : null;
        double ElapsedSeconds() => external?.ElapsedSeconds ?? scenario!.ElapsedSeconds;
        var worker = provider.GetRequiredService<MeasurementWorker>();
        var phases = new List<object>();
        var started = DateTimeOffset.UtcNow;
        var passed = false;
        string? failure = null;
        DateTimeOffset? stoppedAt = null;
        DateTimeOffset? restartedAt = null;
        var disconnectedSnapshotPath = Path.Combine(root, "disconnected-ready.json");
        async Task WaitAsync(Func<PlantOpsDbContext, Task<bool>> predicate)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                external?.EnsureRunning();
                if (disk.AvailableFreeSpace < 2L * 1024 * 1024 * 1024 || initialFree - disk.AvailableFreeSpace > 256L * 1024 * 1024)
                {
                    throw new InvalidOperationException("Runtime experiment disk budget exceeded.");
                }
                await using var scope = provider.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
                if (await db.MeasurementReadings.CountAsync(ct) > (liveHeating ? 3000 : 500))
                {
                    throw new InvalidOperationException("Runtime experiment sample budget exceeded.");
                }
                if (await predicate(db))
                {
                    return;
                }
                await Task.Delay(250, ct);
            }
        }
        try
        {
            if (external != null)
            {
                await external.StartAsync(ct);
            }
            else
            {
                await application!.StartAsync(server);
            }
            await worker.StartAsync(ct);
            if (liveHeating)
            {
                await WaitAsync(_ => Task.FromResult(ElapsedSeconds() >= 150));
                await using var liveScope = provider.CreateAsyncScope();
                var liveDb = liveScope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
                async Task SaveLive(string name)
                {
                    liveDb.ChangeTracker.Clear();
                    await File.WriteAllTextAsync(Path.Combine(root, name), JsonSerializer.Serialize(new
                    {
                        StartedAt = started,
                        At = DateTimeOffset.UtcNow,
                        Schema = parsed.SearchPath,
                        ObservationSimulated = true,
                        RealOpcUa = true,
                        Provider = "PlantOps original scenario / OPC Foundation SDK",
                        Scenario = "p61-live-heating-v1: 60 at 0s, 75 at 60s, 90 at 120s; reference 62; independent vibration",
                        SourceId = sourceId,
                        PointIds = pointIds,
                        Readings = await liveDb.MeasurementReadings.AsNoTracking().Where(x => pointIds.Contains(x.PointId)).ToArrayAsync(ct),
                        Alarms = await liveDb.Alarms.AsNoTracking().Where(x => pointIds.Contains(x.PointId)).ToArrayAsync(ct)
                    }, new JsonSerializerOptions { WriteIndented = true }), ct);
                }
                await SaveLive("live-ready.json");
                await WaitAsync(_ => Task.FromResult(File.Exists(Path.Combine(root, "release"))));
                await SaveLive("live-final.json");
                return;
            }
            await WaitAsync(db => db.Alarms.AnyAsync(alarm => alarm.State == "Active" && pointIds.Contains(alarm.PointId), ct));
            phases.Add(new
            {
                Phase = "ThreePointCollectionAndActiveAlarm",
                At = DateTimeOffset.UtcNow
            });
            if (scenario != null)
            {
                await WaitAsync(_ => Task.FromResult(ElapsedSeconds() >= (industrial ? 39 : MinimalScenario.StopAtSecond)));
                await using var scope = provider.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
                var connected = await db.DataSourceHealth.AsNoTracking().SingleAsync(health => health.DataSourceId == sourceId, ct);
                var observed = await db.MeasurementReadings.AsNoTracking().Where(reading => pointIds.Contains(reading.PointId)).ToArrayAsync(ct);
                var now = DateTimeOffset.UtcNow;
                phases.Add(new
                {
                    Phase = "BeforeStopObservationSemantics",
                    At = now,
                    Health = connected,
                    SessionEvidence = "Current source health and Session KeepAlive; not independent Subscription KeepAlive",
                    SubscriptionEvidence = "Created subscription revisions and actual per-point DataChange readings",
                    Points = pointIds.Select(id =>
                    {
                        var ordered = observed.Where(reading => reading.PointId == id).OrderBy(reading => reading.ReceivedAt).ToArray();
                        var latest = ordered.Last();
                        var notificationFresh = now - latest.ReceivedAt <= TimeSpan.FromSeconds(5);
                        var sourceFresh = latest.SourceTimestamp is { } time && time <= now && now - time <= TimeSpan.FromSeconds(5);
                        return new
                        {
                            PointId = id,
                            latest.ReceivedAt,
                            latest.SourceTimestamp,
                            NotificationFresh = notificationFresh,
                            SourceTimestampFresh = sourceFresh,
                            ValueChanged = ordered.Length > 1 && ordered[^2].NumericValue != latest.NumericValue,
                            DataTrustworthy = latest.Quality == "Good" && !latest.TimeAnomaly && notificationFresh && sourceFresh,
                            Scope = industrial ? "Latest protocol projection only; device-state age and physical observation freshness unknown"
                                : "Latest simulated observation only; no physical coverage inference"
                        };
                    }).ToArray()
                });
            }
            stoppedAt = disconnected ? DateTimeOffset.UtcNow
                : external?.StartedAt.AddSeconds(MinimalScenario.StopAtSecond) ?? DateTimeOffset.UtcNow;
            if (disconnected)
            {
                await external!.DisposeAsync();
            }
            if (server != null)
            {
                await server.StopAsync();
                server.Dispose();
            }
            await WaitAsync(db => db.DataSourceHealth.AnyAsync(health => health.DataSourceId == sourceId && health.State == "Reconnecting", ct));
            phases.Add(new
            {
                Phase = "ServerStoppedAndReconnecting",
                At = DateTimeOffset.UtcNow
            });
            if (disconnected)
            {
                await using var scope = provider.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
                var outage = await db.DataSourceHealth.AsNoTracking().SingleAsync(health => health.DataSourceId == sourceId, ct);
                var outageAlarm = await db.Alarms.AsNoTracking().SingleAsync(alarm => alarm.PointId == pointIds[0], ct);
                var outageReadings = await db.MeasurementReadings.AsNoTracking().Where(reading => pointIds.Contains(reading.PointId)).ToArrayAsync(ct);
                if (outage.State != "Reconnecting" || outageAlarm.State != "Active" || outageAlarm.RecoveredAt != null
                    || !pointIds.All(id => outageReadings.Any(reading => reading.PointId == id && reading.SourceKind == "OpcUa"
                        && reading.Quality == "Good" && !reading.TimeAnomaly)))
                {
                    throw new InvalidOperationException("The outage must preserve an active alarm and prior trusted observations for all three points.");
                }

                var readyTemp = disconnectedSnapshotPath + ".tmp";
                await File.WriteAllTextAsync(readyTemp, JsonSerializer.Serialize(new
                {
                    Passed = true,
                    RealOpcUa = true,
                    Schema = parsed.SearchPath,
                    ScenarioVersion = "plantops-disconnected-v1",
                    CapturedAt = DateTimeOffset.UtcNow,
                    StoppedAt = stoppedAt,
                    Health = outage,
                    Alarms = new[] { outageAlarm },
                    LastGood = pointIds.Select(id => outageReadings.Where(reading => reading.PointId == id
                        && reading.SourceKind == "OpcUa" && reading.Quality == "Good" && !reading.TimeAnomaly)
                        .MaxBy(reading => reading.EffectiveAt))
                }, new JsonSerializerOptions { WriteIndented = true }), ct);
                File.Move(readyTemp, disconnectedSnapshotPath);
                await WaitAsync(_ => Task.FromResult(File.Exists(Path.Combine(root, "release"))));
            }
            else
            {
                if (scenario != null)
                {
                    await WaitAsync(_ => Task.FromResult(ElapsedSeconds() >= MinimalScenario.RestartAtSecond));
                    await using var scope = provider.CreateAsyncScope();
                    var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
                    if (!await db.Alarms.AnyAsync(alarm => alarm.PointId == pointIds[0] && alarm.State == "Active", ct))
                    {
                        throw new InvalidOperationException("Stopping the server must not recover the temperature alarm.");
                    }
                }
                else
                {
                    await Task.Delay(2500, ct);
                }
                restartedAt = external?.StartedAt.AddSeconds(MinimalScenario.RestartAtSecond) ?? DateTimeOffset.UtcNow;
                if (!industrial)
                {
                    server = new SimulatorServer(new(ReorderNamespaces: true, Constant: true), scenario);
                    await application!.StartAsync(server);
                }
                await WaitAsync(async db => (briefLow
                        ? ElapsedSeconds() >= 66
                        : await db.Alarms.AnyAsync(alarm => alarm.State == "Recovered" && pointIds.Contains(alarm.PointId), ct))
                    && await db.SamplingProfiles.CountAsync(profile => pointIds.Contains(profile.PointId), ct) >= 6);
                phases.Add(new
                {
                    Phase = briefLow ? "BriefLowObservationWindowCompleted"
                        : industrial ? "IndustrialSimRestartedAndRecoveredAlarm" : "NamespaceReorderedAndRecoveredAlarm",
                    At = DateTimeOffset.UtcNow
                });
            }
            passed = true;
        }
        catch (Exception exception)
        {
            failure = exception.GetType().Name;
        }
        finally
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await worker.StopAsync(stop.Token);
            if (server != null)
            {
                await server.StopAsync();
                server.Dispose();
            }
            if (external != null)
            {
                await external.DisposeAsync();
            }
        }
        await using var finalScope = provider.CreateAsyncScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
        var readings = await finalDb.MeasurementReadings.AsNoTracking().Where(reading => pointIds.Contains(reading.PointId)).ToArrayAsync();
        var profiles = await finalDb.SamplingProfiles.AsNoTracking().Where(profile => pointIds.Contains(profile.PointId)).ToArrayAsync();
        var gaps = await finalDb.IngestionGaps.AsNoTracking().Where(gap => pointIds.Contains(gap.PointId)).ToArrayAsync();
        var health = await finalDb.DataSourceHealth.AsNoTracking().SingleAsync(health => health.DataSourceId == sourceId);
        var alarms = await finalDb.Alarms.AsNoTracking().Where(alarm => pointIds.Contains(alarm.PointId)).ToArrayAsync();
        passed &= external == null || external.CompletedSuccessfully;
        var postPulseTemperature = readings.Where(reading => reading.PointId == pointIds[0]
            && reading.SourceTimestamp > restartedAt).OrderBy(reading => reading.SourceTimestamp).ToArray();
        var lowSamples = postPulseTemperature.Where(reading => reading.NumericValue < 75).ToArray();
        var briefLowObserved = lowSamples.Length > 0 && postPulseTemperature.Any(reading =>
            reading.NumericValue > 80 && reading.SourceTimestamp > lowSamples[^1].SourceTimestamp);
        passed &= readings.Select(reading => reading.PointId).Distinct().Count() == 3 && readings.All(reading => reading.SourceKind == "OpcUa")
            && profiles.All(profile => profile.EffectiveTo != null) && health.ReconnectCount > 0 && health.State == "Stopped"
            && alarms.Length == 1 && (disconnected
                ? File.Exists(disconnectedSnapshotPath) && alarms[0].State == "Active" && alarms[0].RecoveredAt == null
                : profiles.Length >= 6 && gaps.Length >= 3 && gaps.All(gap => gap.To != null)
                    && (briefLow ? briefLowObserved && alarms[0].State == "Active" && alarms[0].RecoveredAt == null
                        : alarms[0].State == "Recovered"));
        var scenarioChecks = scenario == null ? null : new
        {
            TemperatureStages = new[] { 60.0, 75.0, 90.0 }.All(value => readings.Any(reading =>
                reading.PointId == pointIds[0] && reading.NumericValue == value && reading.ReceivedAt < stoppedAt)),
            IndependentVibration = new[] { 4.0, 12.6, 7.0 }.All(value => readings.Any(reading =>
                reading.PointId == pointIds[2] && reading.NumericValue == value && reading.ReceivedAt < stoppedAt)),
            ConstantUpdatesTime = readings.Where(reading => reading.PointId == pointIds[1]).All(reading => reading.NumericValue == 62)
                && readings.Where(reading => reading.PointId == pointIds[1]).Select(reading => reading.SourceTimestamp).Distinct().Count() > 5,
            UnitsQualityAndTime = readings.All(reading => reading.CanonicalUnit == MinimalScenario.Points[Array.IndexOf(pointIds, reading.PointId)].Unit
                && reading.Quality == "Good" && !reading.TimeAnomaly && reading.TimestampOrigin == "Source"),
            MatchesActualGeneration = industrial ? (bool?)null : readings.All(reading => scenario.Generated.Any(generated =>
                generated.Identifier == MinimalScenario.Points[Array.IndexOf(pointIds, reading.PointId)].Identifier
                && generated.SourceTimestamp == reading.SourceTimestamp && generated.Value == reading.NumericValue)),
            AllPointsAfterRestart = pointIds.All(id => readings.Any(reading => reading.PointId == id && reading.SourceTimestamp > restartedAt))
        };
        if (scenarioChecks != null)
        {
            passed &= scenarioChecks.TemperatureStages && scenarioChecks.IndependentVibration && scenarioChecks.ConstantUpdatesTime
                && scenarioChecks.UnitsQualityAndTime && scenarioChecks.MatchesActualGeneration != false
                && (disconnected || scenarioChecks.AllPointsAfterRestart);
        }
        // Leave the retained experiment inert; never disable or alter business configuration.
        await finalDb.SourceMappings.Where(mapping => mapping.DataSourceId == sourceId).ExecuteUpdateAsync(update => update.SetProperty(mapping => mapping.Enabled, false));
        await finalDb.DataSources.Where(source => source.Id == sourceId).ExecuteUpdateAsync(update => update.SetProperty(source => source.Enabled, false));
        await File.WriteAllTextAsync(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new
        {
            Passed = passed,
            StartedAt = started,
            CompletedAt = DateTimeOffset.UtcNow,
            Schema = parsed.SearchPath,
            ObservationSimulated = true,
            RealOpcUa = true,
            PaidModelCalls = 0,
            Worker = "MeasurementWorker",
            Phases = phases,
            Readings = readings,
            SamplingProfiles = profiles,
            Gaps = gaps,
            Health = health,
            Alarms = alarms,
            Failure = failure,
            ScenarioVersion = disconnected ? "plantops-disconnected-v1" : briefLow ? "plantops-brief-low-v1" : scenario == null ? null : MinimalScenario.Version,
            Seed = scenario == null ? (int?)null : 0,
            ScenarioStartedAt = external?.StartedAt ?? scenario?.StartedAt,
            StoppedAt = stoppedAt,
            RestartedAt = restartedAt,
            ScenarioChecks = scenarioChecks,
            PointContracts = scenario == null ? null : MinimalScenario.Points.Select((point, index) =>
                point with
                {
                    Identifier = approved.Nodes[index]["String:".Length..]
                }),
            Generated = industrial ? null : scenario?.Generated,
            ObservationProvider = industrial ? "industrial-device-simulation-framework" : "PlantOps spike",
            Endpoint = approved.Url,
            NamespaceUri = approved.NamespaceUri,
            SourceTimestampMeaning = industrial ? "Adapter projection time of StateStore, not new physical sampling" : "Simulator generation time",
            ServerTimelineTimesArePlanned = industrial,
            NotificationContract = "StatusValueTimestamp; count actual generated/received only, no sampling-based physical coverage or lost sample claim",
            SubscriptionKeepAliveIndependentlyObserved = false,
            FullP2Accepted = false
        }, new JsonSerializerOptions { WriteIndented = true }));
        if (!passed)
        {
            throw new InvalidOperationException("Runtime acceptance failed; inspect retained result.json.");
        }
        Console.WriteLine($"Three-point Worker reconnect accepted: {readings.Length} samples; evidence {root}");
    }
}
