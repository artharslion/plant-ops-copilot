using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PlantOps.Application.Telemetry;
using PlantOps.Domain.Telemetry;
using PlantOps.Infrastructure.Identity;
using PlantOps.Infrastructure.Persistence;
using PlantOps.Integrations.OpcUa;

namespace PlantOps.Worker;

public static class OpcUaDemo
{
    public static async Task RunAsync(IServiceProvider services, IConfiguration settings, string environment,
        CancellationToken cancellationToken)
    {
        var run = settings["OpcUaDemo:Run"] ?? "";
        var seconds = settings.GetValue("OpcUaDemo:Seconds", 50);
        var aggregateMinutes = settings.GetValue("OpcUaDemo:AggregateMinutes", false);
        var endpoint = settings["OpcUaDemo:Endpoint"] ?? "opc.tcp://127.0.0.1:48481";
        var connection = new NpgsqlConnectionStringBuilder(settings.GetConnectionString("PlantOps"));
        if (environment != "Development" || connection.Database != "plantops_tests"
            || run.Length is < 1 or > 40 || run.Any(x => !char.IsAsciiLetterLower(x) && !char.IsAsciiDigit(x) && x != '_')
            || seconds is < 30 or > 60)
        {
            throw new InvalidOperationException("Finite demo requires Development, plantops_tests, a new lowercase run and 30–60 seconds.");
        }
        OpcUaDemoSource.ValidateEndpoint(endpoint);
        var schema = "opcua_demo_" + run;
        if (connection.SearchPath != schema)
        {
            throw new InvalidOperationException("Demo connection must select only its new opcua_demo_<run> schema.");
        }
        var root = Path.GetFullPath(Path.Combine("artifacts", "opcua-demo", run));
        if (Directory.Exists(root))
        {
            throw new InvalidOperationException("Demo evidence directory already exists; choose a new run.");
        }
        var disks = new[] { Path.GetPathRoot(root)!, Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))! }
            .Distinct(StringComparer.OrdinalIgnoreCase).Select(path => new DriveInfo(path)).ToArray();
        var initialFree = disks.ToDictionary(disk => disk.Name, disk => disk.AvailableFreeSpace);
        bool WithinDiskBudget() => disks.All(disk => disk.AvailableFreeSpace >= 2L * 1024 * 1024 * 1024
            && initialFree[disk.Name] - disk.AvailableFreeSpace <= 256L * 1024 * 1024);
        if (!WithinDiskBudget())
        {
            throw new InvalidOperationException("Insufficient demo evidence disk reserve.");
        }
        Directory.CreateDirectory(root);
        settings["Documents:StorageRoot"] = Path.Combine(root, "documents");
        settings["Demo:SeedHistory"] = "false";
        SourceConfiguration configuration;
        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
            // Schema is strictly validated above; CREATE without IF NOT EXISTS rejects every reused run.
            var create = $"CREATE SCHEMA \"{schema}\"";
            await db.Database.ExecuteSqlRawAsync(create, cancellationToken);
            await DevelopmentIdentityInitializer.InitializeAsync(scope.ServiceProvider, settings, environment);
            var point = await db.MeasurementPoints.SingleAsync(x => x.AssetId == Guid.Parse("30000000-0000-0000-0000-000000000001")
                && x.Code == "VIBRATION_RMS", cancellationToken);
            await db.SourceMappings.ExecuteUpdateAsync(x => x.SetProperty(m => m.Enabled, false), cancellationToken);
            await db.DataSources.ExecuteUpdateAsync(x => x.SetProperty(s => s.Enabled, false), cancellationToken);
            var source = new DataSource
            {
                Id = Guid.NewGuid(),
                PlantId = point.PlantId,
                Name = "IndustrialSim 仿真观测；真实 OPC UA 通信；有限回环演示",
                Kind = "OpcUa",
                Enabled = true,
                ConnectionParameters = JsonSerializer.Serialize(new OpcUaConnectionInput("industrial-sim-demo"))
            };
            var node = new OpcUaMappingInput("urn:industrial-sim:runtime", "String", "plantops-p101/vibration");
            var mapping = new SourceMapping
            {
                Id = Guid.NewGuid(),
                PlantId = point.PlantId,
                PointId = point.Id,
                DataSourceId = source.Id,
                SourceKey = OpcUaConfigurationPolicy.Key(node),
                SourceParameters = JsonSerializer.Serialize(node),
                Enabled = true
            };
            db.DataSources.Add(source);
            db.SourceMappings.Add(mapping);
            await db.SaveChangesAsync(cancellationToken);
            configuration = (await scope.ServiceProvider.GetRequiredService<ISourceConfigurationReader>().GetEnabledAsync(cancellationToken)).Single();
        }
        var failures = new ConcurrentQueue<string>();
        var start = DateTimeOffset.UtcNow;
        var sourceAdapter = new OpcUaDemoSource(endpoint, Path.Combine(root, "certificates"),
            async (revision, ct) =>
            {
                await using var scope = services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                var current = (await scope.ServiceProvider.GetRequiredService<ISourceConfigurationReader>().GetEnabledAsync(ct)).Single();
                if (current != configuration)
                {
                    throw new InvalidOperationException("Demo mapping changed before sampling activation.");
                }
                var history = await db.SamplingProfiles.Where(x => x.PointId == configuration.PointId).ToListAsync(ct);
                var next = SamplingProfileTransitions.Activate(history, revision);
                foreach (var existing in history)
                {
                    existing.EffectiveTo = next.Single(x => x.Id == existing.Id).EffectiveTo;
                }
                db.SamplingProfiles.Add(next.Last());
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            },
            async (at, ct) =>
            {
                await using var scope = services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
                var history = await db.SamplingProfiles.Where(x => x.PointId == configuration.PointId).ToListAsync(ct);
                var next = SamplingProfileTransitions.Close(history, configuration.PointId, at);
                foreach (var existing in history)
                {
                    existing.EffectiveTo = next.Single(x => x.Id == existing.Id).EffectiveTo;
                }
                await db.SaveChangesAsync(ct);
            });
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
        var submitted = 0;
        var retryDuplicates = 0;
        var runner = new MeasurementSourceRunner(sourceAdapter, [configuration],
            async (batch, ct) =>
            {
                if (!WithinDiskBudget() || submitted + batch.Count > 100)
                {
                    deadline.Cancel();
                    throw new InvalidOperationException("Demo disk or sample budget reached.");
                }
                await using var scope = services.CreateAsyncScope();
                var ingestor = scope.ServiceProvider.GetRequiredService<IMeasurementIngestor>();
                await ingestor.IngestAsync(batch, ct);
                // Exercise an actual immutable batch retry against PostgreSQL.
                var retry = await ingestor.IngestAsync(batch, ct);
                if (retry.Inserted != 0 || retry.Duplicates != batch.Count)
                {
                    throw new InvalidOperationException("Repeated protocol batch was not idempotent.");
                }
                retryDuplicates += retry.Duplicates;
                submitted += batch.Count;
            },
            async (losses, ct) =>
            {
                await using var scope = services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IIngestionGapRecorder>().RecordAsync(losses, ct);
            }, exception => failures.Enqueue(exception.GetType().Name + ": " + exception.Message), TimeProvider.System,
            capacity: 100, batchSize: 10);
        try
        {
            await runner.RunAsync(deadline.Token);
        }
        finally
        {
            await using var scope = services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
            await db.SourceMappings.Where(x => x.Id == configuration.MappingId).ExecuteUpdateAsync(x => x.SetProperty(m => m.Enabled, false));
            await db.DataSources.Where(x => x.Id == configuration.DataSourceId).ExecuteUpdateAsync(x => x.SetProperty(s => s.Enabled, false));
        }
        await using var finalScope = services.CreateAsyncScope();
        var collectionEnd = DateTimeOffset.UtcNow;
        var finalDb = finalScope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
        var raw = await finalDb.MeasurementReadings.AsNoTracking().Where(x => x.PointId == configuration.PointId)
            .OrderBy(x => x.EffectiveAt).ToListAsync(CancellationToken.None);
        var alarms = await finalDb.Alarms.AsNoTracking().Where(x => x.PointId == configuration.PointId).ToListAsync(CancellationToken.None);
        var profiles = await finalDb.SamplingProfiles.AsNoTracking().Where(x => x.PointId == configuration.PointId).ToListAsync(CancellationToken.None);
        var gaps = await finalDb.IngestionGaps.AsNoTracking().Where(x => x.PointId == configuration.PointId).ToListAsync(CancellationToken.None);
        var aggregationCalls = 0;
        if (aggregateMinutes && raw.Count > 0)
        {
            try
            {
                // Stop collection first. Never move the clock or aggregate the current partial minute.
                var last = raw.Max(x => x.EffectiveAt).ToUniversalTime();
                var readyAt = last.AddTicks(-(last.UtcTicks % TimeSpan.TicksPerMinute)).AddMinutes(1);
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                wait.CancelAfter(TimeSpan.FromSeconds(65));
                while (DateTimeOffset.UtcNow < readyAt)
                {
                    if (!WithinDiskBudget())
                    {
                        throw new InvalidOperationException("Demo disk budget reached.");
                    }
                    await Task.Delay(TimeSpan.FromSeconds(1), wait.Token);
                }
                if (!WithinDiskBudget())
                {
                    throw new InvalidOperationException("Demo disk budget reached.");
                }
                using var aggregate = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                aggregate.CancelAfter(TimeSpan.FromSeconds(30));
                aggregationCalls++;
                await finalScope.ServiceProvider.GetRequiredService<IMeasurementMaintenance>().AggregateAsync(aggregate.Token);
            }
            catch (Exception exception)
            {
                // Preserve a bounded, sanitized failure report; do not start retention or retry.
                failures.Enqueue("MinuteAggregation: " + exception.GetType().Name);
            }
        }
        var minutes = await finalDb.MeasurementMinutes.AsNoTracking().Where(x => x.PointId == configuration.PointId)
            .OrderBy(x => x.BucketStart).ToListAsync(CancellationToken.None);
        var rawCountAfterAggregation = await finalDb.MeasurementReadings.CountAsync(x => x.PointId == configuration.PointId, CancellationToken.None);
        var passed = raw.Any(x => x.NumericValue == 4) && raw.Any(x => x.NumericValue == 12.6) && raw.Any(x => x.NumericValue == 6)
            && alarms.Count == 1 && alarms[0].State == "Recovered" && profiles.Count > 0 && profiles.All(x => x.EffectiveTo.HasValue)
            && failures.IsEmpty && raw.Count is > 20 and < 100 && WithinDiskBudget() && retryDuplicates >= raw.Count
            && raw.Select(x => x.SourceEventKey).Distinct().Count() == raw.Count
            && raw.All(x => x.SourceKind == "OpcUa" && x.RawStatusCode.HasValue && x.SourceTimestamp.HasValue)
            && rawCountAfterAggregation == raw.Count
            && (!aggregateMinutes || aggregationCalls == 1 && minutes.Count > 0 && minutes.Sum(x => x.SampleCount) == raw.Count);
        await File.WriteAllTextAsync(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new
        {
            Passed = passed,
            Schema = schema,
            Start = start,
            End = collectionEnd,
            CompletedAt = DateTimeOffset.UtcNow,
            Source = "IndustrialSim OPC UA, None, loopback-only finite demo",
            RetryDuplicates = retryDuplicates,
            Configuration = configuration,
            Readings = raw,
            Alarms = alarms,
            SamplingProfiles = profiles,
            Gaps = gaps,
            AggregateMinutes = aggregateMinutes,
            AggregationCalls = aggregationCalls,
            MeasurementMinutes = minutes,
            RawCountAfterAggregation = rawCountAfterAggregation,
            Failures = failures.ToArray(),
            DiskObservations = disks.Select(disk => new
            {
                disk.Name,
                InitialFreeBytes = initialFree[disk.Name],
                FinalFreeBytes = disk.AvailableFreeSpace,
                ObservedGrowthBytes = initialFree[disk.Name] - disk.AvailableFreeSpace,
                MinimumFreeBytes = 2L * 1024 * 1024 * 1024,
                MaximumGrowthBytes = 256L * 1024 * 1024
            }),
            LongRunningCapacityAccepted = false
        }, new JsonSerializerOptions { WriteIndented = true }), CancellationToken.None);
        if (!passed)
        {
            throw new InvalidOperationException("OPC UA demo acceptance failed; inspect its retained result.json.");
        }
        Console.WriteLine($"OPC UA normal/high/recovery accepted: {raw.Count} readings, one recovered alarm. Evidence: {root}");
    }
}
