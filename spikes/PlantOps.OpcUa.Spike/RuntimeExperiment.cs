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

    public static async Task RunAsync(string root)
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
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(150));
        var ct = deadline.Token;
        var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:PlantOps"] = connection,
            ["Demo:Password"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)) + "a!",
            ["Demo:SeedHistory"] = "false",
            ["Documents:StorageRoot"] = Path.Combine(root, "documents"),
            ["Telemetry:SimulationEnabled"] = "false"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<IConfiguration>(settings);
        services.AddSingleton<IHostEnvironment>(new RuntimeEnvironment(root));
        services.AddPlantOpsInfrastructure(settings);
        var approved = new OpcUaApprovedRuntimeEndpoint(Program.Endpoint, "urn:localhost:plantops:spike:server", Program.NamespaceUri,
            ["String:North.Utilities.P101.VibrationRms", "String:North.Point001.VibrationRms", "String:North.Point002.VibrationRms"],
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
            db.MeasurementPoints.AddRange(points.Skip(1));
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
                var mapping = new OpcUaMappingInput(Program.NamespaceUri, "String", approved.Nodes[index]["String:".Length..]);
                db.SourceMappings.Add(new SourceMapping
                {
                    Id = Guid.NewGuid(),
                    PlantId = point.PlantId,
                    PointId = points[index].Id,
                    DataSourceId = sourceId,
                    SourceKey = OpcUaConfigurationPolicy.Key(mapping),
                    SourceParameters = JsonSerializer.Serialize(mapping),
                    Enabled = true
                });
            }
            await db.SaveChangesAsync(ct);
        }
        var telemetry = new SpikeTelemetry();
        var application = await Program.ConfigureAsync("Server", Path.Combine(root, "server"), telemetry, localRuntime: true);
        var server = new SimulatorServer(new(Constant: false));
        var worker = provider.GetRequiredService<MeasurementWorker>();
        var phases = new List<object>();
        var started = DateTimeOffset.UtcNow;
        var passed = false;
        string? failure = null;
        async Task WaitAsync(Func<PlantOpsDbContext, Task<bool>> predicate)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (disk.AvailableFreeSpace < 2L * 1024 * 1024 * 1024 || initialFree - disk.AvailableFreeSpace > 256L * 1024 * 1024)
                {
                    throw new InvalidOperationException("Runtime experiment disk budget exceeded.");
                }
                await using var scope = provider.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
                if (await db.MeasurementReadings.CountAsync(ct) > 500)
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
            await application.StartAsync(server);
            await worker.StartAsync(ct);
            await WaitAsync(db => db.Alarms.AnyAsync(alarm => alarm.State == "Active" && pointIds.Contains(alarm.PointId), ct));
            phases.Add(new
            {
                Phase = "ThreePointCollectionAndActiveAlarm",
                At = DateTimeOffset.UtcNow
            });
            await server.StopAsync();
            server.Dispose();
            await WaitAsync(db => db.DataSourceHealth.AnyAsync(health => health.DataSourceId == sourceId && health.State == "Reconnecting", ct));
            phases.Add(new
            {
                Phase = "ServerStoppedAndReconnecting",
                At = DateTimeOffset.UtcNow
            });
            await Task.Delay(2500, ct);
            server = new SimulatorServer(new(ReorderNamespaces: true, Constant: true));
            await application.StartAsync(server);
            await WaitAsync(async db => await db.Alarms.AnyAsync(alarm => alarm.State == "Recovered" && pointIds.Contains(alarm.PointId), ct)
                && await db.SamplingProfiles.CountAsync(profile => pointIds.Contains(profile.PointId), ct) >= 6);
            phases.Add(new
            {
                Phase = "NamespaceReorderedAndRecoveredAlarm",
                At = DateTimeOffset.UtcNow
            });
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
            await server.StopAsync();
            server.Dispose();
        }
        await using var finalScope = provider.CreateAsyncScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
        var readings = await finalDb.MeasurementReadings.AsNoTracking().Where(reading => pointIds.Contains(reading.PointId)).ToArrayAsync();
        var profiles = await finalDb.SamplingProfiles.AsNoTracking().Where(profile => pointIds.Contains(profile.PointId)).ToArrayAsync();
        var gaps = await finalDb.IngestionGaps.AsNoTracking().Where(gap => pointIds.Contains(gap.PointId)).ToArrayAsync();
        var health = await finalDb.DataSourceHealth.AsNoTracking().SingleAsync(health => health.DataSourceId == sourceId);
        var alarms = await finalDb.Alarms.AsNoTracking().Where(alarm => pointIds.Contains(alarm.PointId)).ToArrayAsync();
        passed &= readings.Select(reading => reading.PointId).Distinct().Count() == 3 && readings.All(reading => reading.SourceKind == "OpcUa")
            && profiles.Length >= 6 && profiles.All(profile => profile.EffectiveTo != null) && gaps.Length >= 3
            && gaps.All(gap => gap.To != null) && health.ReconnectCount > 0 && health.State == "Stopped"
            && alarms.Length == 1 && alarms[0].State == "Recovered";
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
            FullP2Accepted = false
        }, new JsonSerializerOptions { WriteIndented = true }));
        if (!passed)
        {
            throw new InvalidOperationException("Runtime acceptance failed; inspect retained result.json.");
        }
        Console.WriteLine($"Three-point Worker reconnect accepted: {readings.Length} samples; evidence {root}");
    }
}
