using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Opc.Ua;
using Opc.Ua.Client;
using PlantOps.Application.Telemetry;
using PlantOps.Domain.Assets;
using PlantOps.Domain.Telemetry;
using PlantOps.Infrastructure.Persistence;
using PlantOps.Integrations.OpcUa;

namespace PlantOps.OpcUa.Spike;

// Finite isolated reads and storage sizing only. No IMeasurementSource, Worker registration,
// enabled source, sampling-profile inference, retention, or business ingestion lifecycle.
internal static class CapacityExperiment
{
    private static bool SameTime(DateTimeOffset? left, DateTimeOffset? right) =>
        left.HasValue == right.HasValue && (!left.HasValue || left.Value.UtcTicks / 10 == right!.Value.UtcTicks / 10);

    private sealed record Probe(DateTimeOffset At, string Stage, long Rows, long SchemaBytes,
        long RawTableBytes, long RawIndexBytes, long WalDirectoryBytes, long WalGeneratedBytes,
        long CFreeBytes, long DFreeBytes);

    public static async Task RunAsync(ISession session, string root)
    {
        var connection = Environment.GetEnvironmentVariable("PLANTOPS_CAPACITY_CONNECTION")
            ?? throw new ArgumentException("Dedicated capacity connection required.");
        var schema = Environment.GetEnvironmentVariable("PLANTOPS_CAPACITY_SCHEMA") ?? "";
        CapacityLimits.ValidateConnection(connection, schema);
        var artifactParent = Path.GetFullPath("artifacts/p2") + Path.DirectorySeparatorChar;
        if (!root.StartsWith(artifactParent, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Capacity evidence must stay under artifacts/p2.");
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        var ct = timeout.Token;
        var watch = Stopwatch.StartNew();
        await using var sql = new NpgsqlConnection(connection);
        await sql.OpenAsync(ct);
        var startLsn = (string)(await new NpgsqlCommand("SELECT pg_current_wal_insert_lsn()::text", sql).ExecuteScalarAsync(ct))!;
        var probes = new List<Probe>();
        await using var log = new StreamWriter(Path.Combine(root, "capacity-probes.jsonl"), append: false);

        async Task<Probe> MeasureAsync(string stage, bool tablesExist)
        {
            var query = """
                SELECT json_build_object(
                  'SchemaBytes', (SELECT coalesce(sum(pg_total_relation_size(c.oid)),0) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname=@schema AND c.relkind IN ('r','m')),
                  'WalDirectoryBytes', (SELECT coalesce(sum(size),0) FROM pg_ls_waldir()),
                  'WalGeneratedBytes', pg_wal_lsn_diff(pg_current_wal_insert_lsn(), @lsn::pg_lsn)::bigint
                )::text
                """;
            await using var command = new NpgsqlCommand(query, sql);
            command.Parameters.AddWithValue("schema", schema);
            command.Parameters.AddWithValue("lsn", startLsn);
            using var metrics = JsonDocument.Parse((string)(await command.ExecuteScalarAsync(ct))!);
            var m = metrics.RootElement;
            long rows = 0, table = 0, indexes = 0;
            if (tablesExist)
            {
                await using var raw = new NpgsqlCommand("""
                    SELECT (SELECT count(*) FROM "MeasurementReadings"),
                      coalesce(sum(pg_table_size(relid)),0)::bigint,
                      coalesce(sum(pg_indexes_size(relid)),0)::bigint
                    FROM pg_partition_tree('"MeasurementReadings"'::regclass) WHERE isleaf
                    """, sql);
                await using var reader = await raw.ExecuteReaderAsync(ct);
                await reader.ReadAsync(ct);
                rows = reader.GetInt64(0);
                table = reader.GetInt64(1);
                indexes = reader.GetInt64(2);
            }
            var probe = new Probe(DateTimeOffset.UtcNow, stage, rows, m.GetProperty("SchemaBytes").GetInt64(),
                table, indexes, m.GetProperty("WalDirectoryBytes").GetInt64(), m.GetProperty("WalGeneratedBytes").GetInt64(),
                new DriveInfo("C").AvailableFreeSpace, new DriveInfo("D").AvailableFreeSpace);
            probes.Add(probe);
            await log.WriteLineAsync(JsonSerializer.Serialize(probe));
            await log.FlushAsync(ct);
            var baseline = probes[0];
            CapacityLimits.Check(rows, probe.SchemaBytes, probe.WalGeneratedBytes,
                probe.WalDirectoryBytes - baseline.WalDirectoryBytes,
                Math.Max(baseline.CFreeBytes - probe.CFreeBytes, baseline.DFreeBytes - probe.DFreeBytes),
                Math.Min(probe.CFreeBytes, probe.DFreeBytes));
            return probe;
        }

        try
        {
            await MeasureAsync("before-schema", false);
            // No IF NOT EXISTS: reusing any existing schema must fail, preserving prior runs.
            await new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", sql).ExecuteNonQueryAsync(ct);
            var dbOptions = new DbContextOptionsBuilder<PlantOpsDbContext>().UseNpgsql(connection).Options;
            await using (var db = new PlantOpsDbContext(dbOptions))
            {
                await db.Database.MigrateAsync(ct);
            }
            await MeasureAsync("after-migration", true);
            var plant = Guid.NewGuid();
            var location = Guid.NewGuid();
            var asset = Guid.NewGuid();
            var source = Guid.NewGuid();
            var configurations = new List<SourceConfiguration>();
            await using (var db = new PlantOpsDbContext(dbOptions))
            {
                db.Plants.Add(new Plant { Id = plant, Code = "CAPACITY", Name = "Isolated OPC UA capacity" });
                db.Locations.Add(new Location { Id = location, PlantId = plant, Code = "LAB", Name = "Lab" });
                db.Assets.Add(new Asset { Id = asset, PlantId = plant, LocationId = location, Code = "LAB", Name = "Lab", AssetType = "Lab" });
                db.DataSources.Add(new DataSource { Id = source, PlantId = plant, Name = "Disabled finite-read fixture", Kind = "OpcUa", Enabled = false, ConnectionParameters = "{}" });
                for (var i = 0; i < CapacityLimits.Points; i++)
                {
                    var key = i == 0 ? "North.Utilities.P101.VibrationRms" : $"{(i < 50 ? "North" : "South")}.Point{i:D3}.VibrationRms";
                    var point = Guid.NewGuid();
                    var mapping = Guid.NewGuid();
                    var parameters = JsonSerializer.Serialize(new OpcUaMappingInput(Program.NamespaceUri, "String", key));
                    db.MeasurementPoints.Add(new MeasurementPoint { Id = point, PlantId = plant, AssetId = asset, Code = $"P{i:D3}", Name = key });
                    db.SourceMappings.Add(new SourceMapping { Id = mapping, PlantId = plant, PointId = point, DataSourceId = source, SourceKey = key, SourceParameters = parameters, Enabled = false });
                    configurations.Add(new(plant, asset, point, 1, "mm/s", 1000, 5000, source, 1, "OpcUa", mapping, 1, key, "mm/s", 1, 0, parameters));
                }
                await db.SaveChangesAsync(ct);
            }
            var nodes = new ReadValueIdCollection();
            foreach (var config in configurations)
            {
                var mapping = new OpcUaMappingInput(Program.NamespaceUri, "String", config.SourceKey);
                var validation = await OpcUaMetadataReader.ValidateAsync(session, config.SourceKey, mapping, "mm/s", "mm/s", 1, 0, ct);
                if (!validation.Accepted)
                {
                    throw new InvalidOperationException("Capacity node metadata validation failed.");
                }
                nodes.Add(new ReadValueId { NodeId = OpcUaNodeIdentity.Resolve(mapping, session.NamespaceUris), AttributeId = Attributes.Value });
            }
            await MeasureAsync("before-reads", true);
            var expected = new Dictionary<(Guid Point, string Key), MeasurementReading>();
            var dates = new HashSet<DateOnly>();
            var readWatch = Stopwatch.StartNew();
            var duplicates = 0;
            for (var round = 0; round < CapacityLimits.Rounds; round++)
            {
                var remaining = TimeSpan.FromSeconds(round) - readWatch.Elapsed;
                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining, ct);
                }
                await MeasureAsync($"before-read-{round}", true);
                var response = await session.ReadAsync(null, 0, TimestampsToReturn.Both, nodes, ct);
                ClientBase.ValidateResponse(response.Results, nodes);
                var received = DateTimeOffset.UtcNow;
                var batch = new List<MeasurementReading>();
                for (var i = 0; i < configurations.Count; i++)
                {
                    var normalized = OpcUaNormalizer.Normalize(configurations[i], response.Results[i], received, Guid.NewGuid());
                    if (normalized.RawStatusCode != response.Results[i].StatusCode.Code
                        || normalized.StatusSymbol != response.Results[i].StatusCode.ToString())
                    {
                        throw new InvalidOperationException("Normalizer changed the actual protocol status identity.");
                    }
                    var reading = SamplePolicy.ToReading(normalized);
                    if (!expected.TryAdd((reading.PointId, reading.SourceEventKey), reading))
                    {
                        duplicates++;
                        continue;
                    }
                    batch.Add(reading);
                }
                await using (var db = new PlantOpsDbContext(dbOptions))
                {
                    foreach (var day in batch.Select(x => x.EffectiveDate).Distinct().Where(x => dates.Add(x)))
                    {
                        var from = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                        var to = day.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                        var table = "MeasurementReadings_" + day.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
#pragma warning disable EF1002
                        await db.Database.ExecuteSqlRawAsync($"CREATE TABLE \"{table}\" PARTITION OF \"MeasurementReadings\" FOR VALUES FROM ('{from}') TO ('{to}')", ct);
#pragma warning restore EF1002
                        db.MeasurementPartitions.Add(new MeasurementPartition { Date = day });
                    }
                    db.MeasurementReadings.AddRange(batch);
                    await db.SaveChangesAsync(ct);
                }
                await MeasureAsync($"after-write-{round}", true);
            }
            await using var verification = new PlantOpsDbContext(dbOptions);
            var stored = await verification.MeasurementReadings.AsNoTracking().ToListAsync(ct);
            var mismatches = stored.Count(actual => !expected.TryGetValue((actual.PointId, actual.SourceEventKey), out var before)
                || !JsonNode.DeepEquals(JsonNode.Parse(actual.RawValueJson), JsonNode.Parse(before.RawValueJson))
                || actual.RawStatusCode != before.RawStatusCode || actual.StatusSymbol != before.StatusSymbol
                || actual.NumericValue != before.NumericValue || actual.Quality != before.Quality || actual.SourceKind != "OpcUa"
                || !SameTime(actual.SourceTimestamp, before.SourceTimestamp) || !SameTime(actual.ServerTimestamp, before.ServerTimestamp)
                || !SameTime(actual.ReceivedAt, before.ReceivedAt) || !SameTime(actual.EffectiveAt, before.EffectiveAt) || actual.TimestampOrigin != before.TimestampOrigin);
            var qualities = stored.GroupBy(x => x.Quality).ToDictionary(x => x.Key, x => x.Count());
            if (stored.Count != expected.Count || mismatches != 0 || !new[] { "Good", "Uncertain", "Bad" }.All(qualities.ContainsKey)
                || await verification.DataSources.AnyAsync(x => x.Enabled, ct) || await verification.SourceMappings.AnyAsync(x => x.Enabled, ct))
            {
                throw new InvalidOperationException("Persisted fidelity, quality coverage or disabled fixture invariant failed.");
            }
            var final = await MeasureAsync("verified", true);
            var result = new
            {
                Schema = schema,
                Points = CapacityLimits.Points,
                ReadRounds = CapacityLimits.Rounds,
                ProtocolReadValues = CapacityLimits.Points * CapacityLimits.Rounds,
                InsertedRows = stored.Count,
                DuplicateObservations = duplicates,
                FidelityMismatches = mismatches,
                Qualities = qualities,
                ElapsedSeconds = watch.Elapsed.TotalSeconds,
                ReadWindowSeconds = readWatch.Elapsed.TotalSeconds,
                Limits = new
                {
                    CapacityLimits.SchemaBytes,
                    CapacityLimits.WalBytes,
                    CapacityLimits.HostGrowthBytes,
                    DeadlineSeconds = 180
                },
                Final = final,
                ObservedWalDirectoryPeakBytes = probes.Max(x => x.WalDirectoryBytes),
                ObservedWalDirectoryAdditionalPeakBytes = Math.Max(0, probes.Max(x => x.WalDirectoryBytes) - probes[0].WalDirectoryBytes),
                ClusterWalGeneratedBytes = final.WalGeneratedBytes,
                RawTotalBytesPerRow = (double)(final.RawTableBytes + final.RawIndexBytes) / stored.Count,
                RawTableBytesPerRow = (double)final.RawTableBytes / stored.Count,
                RawIndexBytesPerRow = (double)final.RawIndexBytes / stored.Count,
                Examples = stored.GroupBy(x => x.Quality).Select(x => x.First()).Select(x => new { x.Quality, x.RawValueJson, x.RawStatusCode, x.StatusSymbol, x.NumericValue, x.SourceTimestamp, x.ServerTimestamp, x.ReceivedAt }),
                SamplingMode = "Finite explicit Read, maxAge=0, timestamps=Both; no subscription or coverage claim",
                WalScope = "Cluster-wide sampled directory occupancy and insert-LSN delta, including migration and concurrent activity; not instantaneous/full-load peak or solely attributable WAL",
                Scope = "Real SDK DataValues from fictional OPC UA server through existing normalizer/ToReading and migrated partitioned schema; direct EF storage sizing, no business ingestion lifecycle",
                SmallScalePassed = true,
                BudgetComplete = false,
                CapacityPassed = false,
                ContinuousSourceEnabled = false
            };
            await File.WriteAllTextAsync(Path.Combine(root, "capacity-result.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }), ct);
            Console.WriteLine($"Finite capacity experiment passed: {stored.Count} persisted rows; G0 remains unpassed.");
        }
        catch (Exception exception)
        {
            await File.WriteAllTextAsync(Path.Combine(root, "capacity-failure.json"), JsonSerializer.Serialize(new
            {
                Schema = schema,
                FailureType = exception.GetType().Name,
                ElapsedSeconds = watch.Elapsed.TotalSeconds,
                SmallScalePassed = false,
                CapacityPassed = false,
                EvidenceRetained = true
            }));
            throw;
        }
    }
}
