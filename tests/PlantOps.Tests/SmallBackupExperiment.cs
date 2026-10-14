using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PlantOps.Application.Knowledge;
using PlantOps.Domain.Assets;
using PlantOps.Domain.Identity;
using PlantOps.Infrastructure.Identity;
using PlantOps.Infrastructure.Persistence;
using Xunit;

namespace PlantOps.Tests;

// Opt-in local measurement harness. Never called by Worker or production backup paths.
internal static class SmallBackupExperiment
{
    private const string Accepted = "p2_capacity_20261008_accepted";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly Guid North = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid South = Guid.Parse("10000000-0000-0000-0000-000000000002");

    internal static void Check(double seconds, long growth, long walGrowth, long free)
    {
        if (seconds > 180 || growth > 268435456 || walGrowth > 134217728 || free < 2147483648)
        {
            throw new InvalidOperationException("Observed bounded backup limit exceeded.");
        }
    }

    internal static void CheckPayload(long schemaBytes, long dumpBytes, int files, long fileBytes)
    {
        if (schemaBytes > 67108864 || dumpBytes > 33554432 || files > 3 || fileBytes > 1048576)
        {
            throw new InvalidOperationException("Small backup payload limit exceeded.");
        }
    }

    internal static string Remap(string sql, string source, string target)
    {
        if (!Regex.IsMatch(source, "^p2_[a-z0-9_]{1,55}$") || !Regex.IsMatch(target, "^p2_backup_[a-z0-9_]{1,45}$") || source == target)
        {
            throw new InvalidOperationException("Use distinct safe isolated schemas.");
        }
        var output = new StringBuilder();
        var copy = false;
        foreach (var line in sql.Split('\n'))
        {
            output.Append(copy ? line : Regex.Replace(line, $"(?<![a-zA-Z0-9_]){Regex.Escape(source)}(?![a-zA-Z0-9_])", target)).Append('\n');
            if (line.StartsWith("COPY ", StringComparison.Ordinal) && line.TrimEnd().EndsWith("FROM stdin;", StringComparison.Ordinal))
            {
                copy = true;
            }
            else if (copy && line.TrimEnd('\r') == "\\.")
            {
                copy = false;
            }
        }
        if (copy)
        {
            throw new InvalidOperationException("Unterminated COPY payload.");
        }
        return output.ToString();
    }

    private sealed record Probe(string Phase, double Seconds, long CFree, long DFree, long WalBytes, long WalGenerated, long SchemaBytes, long ArtifactBytes);

    private sealed record TableDigest(string Table, long Rows, string Sha256);

    private sealed record FileDigest(string Key, long Bytes, string Sha256, string Sddl);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static async Task RunAsync()
    {
        var run = Environment.GetEnvironmentVariable("PLANTOPS_SMALL_BACKUP_RUN")!;
        Assert.Matches("^[a-z0-9_]{1,30}$", run);
        var root = Path.GetFullPath(Environment.GetEnvironmentVariable("PLANTOPS_SMALL_BACKUP_ROOT")!);
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(repository.FullName, "PlantOps.sln")))
        {
            repository = repository.Parent ?? throw new InvalidOperationException("Repository root unavailable.");
        }
        Assert.Equal(Path.Combine(repository.FullName, "artifacts", "p2", $"backup-{run}"), root, ignoreCase: true);
        var source = $"p2_backup_{run}_source";
        var target = $"p2_backup_{run}_restore";
        var seedRoot = Path.Combine(root, "source-files");
        var restoredRoot = Path.Combine(root, "restored-files");
        var backupRoot = Path.Combine(root, "backup-files");
        Assert.False(Directory.Exists(seedRoot));
        Directory.CreateDirectory(seedRoot);
        Directory.CreateDirectory(backupRoot);
        Directory.CreateDirectory(restoredRoot);
        var builder = new NpgsqlConnectionStringBuilder(TestDatabase.Connection);
        Assert.Equal("plantops_tests", builder.Database);
        builder.SearchPath = "public";
        builder.CommandTimeout = 30;
        await using var db = new NpgsqlConnection(builder.ConnectionString);
        await db.OpenAsync();
        async Task<string> Scalar(string sql)
        {
            await using var command = new NpgsqlCommand(sql, db);
            return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture)!;
        }
        async Task<long> Number(string sql) => long.Parse(await Scalar(sql), System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(0, await Number($"SELECT count(*) FROM pg_namespace WHERE nspname IN ('{source}','{target}')"));
        Assert.Equal(12000, await Number($"SELECT count(*) FROM {Accepted}.\"MeasurementReadings\""));
        Assert.Equal(0, await Number($"SELECT count(*) FROM {Accepted}.\"Documents\""));
        Assert.Equal(0, await Number($"SELECT count(*) FROM {Accepted}.\"DataSources\" WHERE \"Enabled\""));
        Assert.Equal(0, await Number($"SELECT count(*) FROM {Accepted}.\"SourceMappings\" WHERE \"Enabled\""));
        var lsn = await Scalar("SELECT pg_current_wal_insert_lsn()::text");
        var timer = Stopwatch.StartNew();
        var probes = new List<Probe>();
        var phase = "clone";
        async Task Sample()
        {
            var cfree = new DriveInfo("C").AvailableFreeSpace;
            var dfree = new DriveInfo("D").AvailableFreeSpace;
            var wal = await Number("SELECT coalesce(sum(size),0) FROM pg_ls_waldir()");
            var generated = await Number($"SELECT pg_wal_lsn_diff(pg_current_wal_insert_lsn(),'{lsn}')::bigint");
            var schema = await Number($"SELECT coalesce(sum(pg_total_relation_size(c.oid)),0) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname IN ('{source}','{target}') AND c.relkind IN ('r','m')");
            var bytes = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Sum(x => new FileInfo(x).Length);
            var p = new Probe(phase, timer.Elapsed.TotalSeconds, cfree, dfree, wal, generated, schema, bytes);
            probes.Add(p);
            await File.AppendAllTextAsync(Path.Combine(root, "probes.jsonl"), JsonSerializer.Serialize(p) + "\n");
            var first = probes[0];
            Check(p.Seconds, Math.Max(first.CFree - cfree, first.DFree - dfree), Math.Max(generated, wal - first.WalBytes), Math.Min(cfree, dfree));
            Assert.True(schema <= 134217728, "Two isolated schemas exceeded combined 128 MiB.");
            Assert.True(await Number($"SELECT coalesce(max(bytes),0) FROM (SELECT n.nspname,sum(pg_total_relation_size(c.oid)) bytes FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname IN ('{source}','{target}') AND c.relkind IN ('r','m') GROUP BY n.nspname) q") <= 67108864, "One schema exceeded 64 MiB.");
            Assert.True(bytes <= 100663296, "Evidence/files exceeded 96 MiB.");
        }
        async Task<string> Docker(string[] arguments, string? input = null)
        {
            await Sample();
            using var process = new Process { StartInfo = new ProcessStartInfo("docker") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true } };
            foreach (var arg in arguments)
            {
                process.StartInfo.ArgumentList.Add(arg);
            }
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            var write = Task.Run(async () =>
            {
                if (input != null)
                {
                    await process.StandardInput.WriteAsync(input);
                }
                process.StandardInput.Close();
            });
            try
            {
                while (!process.HasExited)
                {
                    await Task.Delay(250);
                    await Sample();
                }
                await write;
                Assert.True(process.ExitCode == 0, await stderr);
                var result = await stdout;
                CheckPayload(0, Encoding.UTF8.GetByteCount(result), 0, 0);
                await Sample();
                return result;
            }
            catch
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                }
                throw;
            }
        }
        Task<string> Dump(string schema) => Docker(["exec", "plantops-database-1", "pg_dump", "-U", "plantops", "-d", "plantops_tests", "--schema", schema, "--format=p", "--no-owner", "--no-privileges"]);
        Task<string> Restore(string sql) => Docker(["exec", "-i", "-e", "PGOPTIONS=-c statement_timeout=30000", "plantops-database-1", "psql", "-U", "plantops", "-d", "plantops_tests", "-X", "-v", "ON_ERROR_STOP=1", "--single-transaction"], sql);
        async Task Save(string name, object value) => await File.WriteAllTextAsync(Path.Combine(root, name), JsonSerializer.Serialize(value, Json));
        async Task<List<TableDigest>> Digests(string schema)
        {
            var names = new List<string>();
            await using (var cmd = new NpgsqlCommand($"SELECT c.relname FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='{schema}' AND c.relkind IN ('r','p') AND NOT c.relispartition ORDER BY c.relname", db))
            await using (var reader = await cmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    names.Add(reader.GetString(0));
                }
            }
            var results = new List<TableDigest>();
            foreach (var table in names)
            {
                var data = await Scalar($"SELECT coalesce(string_agg(j,E'\\n' ORDER BY j),'') FROM (SELECT to_jsonb(t)::text AS j FROM {schema}.\"{table.Replace("\"", "\"\"")}\" t) q");
                results.Add(new(table, await Number($"SELECT count(*) FROM {schema}.\"{table}\""), Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(data)))));
            }
            return results;
        }
        async Task<string> Structure(string schema) => (await Scalar($"SELECT coalesce(jsonb_agg(j ORDER BY j::text)::text,'[]') FROM (SELECT jsonb_build_object('name',c.relname,'kind',c.relkind,'owner',pg_get_userbyid(c.relowner),'acl',c.relacl,'indexes',(SELECT jsonb_agg(pg_get_indexdef(i.indexrelid) ORDER BY pg_get_indexdef(i.indexrelid)) FROM pg_index i WHERE i.indrelid=c.oid),'constraints',(SELECT jsonb_agg(jsonb_build_object('name',k.conname,'def',pg_get_constraintdef(k.oid)) ORDER BY k.conname) FROM pg_constraint k WHERE k.conrelid=c.oid)) j FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='{schema}' AND c.relkind IN ('r','p','S')) q")).Replace(schema + ".", "<schema>.", StringComparison.Ordinal);
        async Task<List<FileDigest>> Files(string schema, string directory)
        {
            var result = new List<FileDigest>();
            await using var cmd = new NpgsqlCommand($"SELECT \"StorageKey\",\"SizeBytes\",\"Sha256\" FROM {schema}.\"DocumentVersions\" ORDER BY \"StorageKey\"", db);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var key = reader.GetString(0);
                Assert.True(Guid.TryParseExact(key, "N", out _));
                var path = Path.Combine(directory, key);
                var bytes = await File.ReadAllBytesAsync(path);
                var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
                Assert.Equal(reader.GetInt64(1), bytes.LongLength);
                Assert.Equal(reader.GetString(2), hash);
                var sddl = new FileInfo(path).GetAccessControl().GetSecurityDescriptorSddlForm(System.Security.AccessControl.AccessControlSections.Access | System.Security.AccessControl.AccessControlSections.Owner | System.Security.AccessControl.AccessControlSections.Group);
                result.Add(new(key, bytes.Length, hash, sddl));
            }
            Assert.Equal(3, result.Count);
            Assert.Equal(3, Directory.GetFiles(directory).Length);
            Assert.True(result.Sum(x => x.Bytes) <= 1048576);
            CheckPayload(0, 0, result.Count, result.Sum(x => x.Bytes));
            return result;
        }
        try
        {
            await Sample();
            var acceptedBefore = await Digests(Accepted);
            await Restore(Remap(await Dump(Accepted), Accepted, source));
            Assert.Equal(acceptedBefore, await Digests(source));
            builder.SearchPath = source;
            var sourceConnection = builder.ConnectionString;
            Environment.SetEnvironmentVariable("PLANTOPS_TEST_DOCUMENT_ROOT", seedRoot);
            phase = "seed";
            async Task InitializeAccount(AssetManagementTests.Session session, string role, Guid plant)
            {
                await Sample();
                session.Client.Timeout = TimeSpan.FromSeconds(10);
                await using var scope = session.Factory.Services.CreateAsyncScope();
                var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
                foreach (var name in new[] { "Admin", "Viewer" })
                {
                    if (!await roles.RoleExistsAsync(name))
                    {
                        Assert.True((await roles.CreateAsync(new IdentityRole(name))).Succeeded);
                    }
                }
                var users = scope.ServiceProvider.GetRequiredService<UserManager<PlantOpsUser>>();
                var user = new PlantOpsUser { UserName = session.Name, DisplayName = "Small backup experiment" };
                Assert.True((await users.CreateAsync(user, session.Password)).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
                var context = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
                if (!await context.Plants.AnyAsync(x => x.Id == plant))
                {
                    context.Plants.Add(new Plant { Id = plant, Code = plant == North ? "BACKUP_NORTH" : "BACKUP_SOUTH", Name = "Isolated backup access fixture" });
                }
                context.UserPlantAccess.Add(new()
                {
                    UserId = user.Id,
                    PlantId = plant,
                    GrantedAt = DateTimeOffset.UtcNow
                });
                await context.SaveChangesAsync();
                await Sample();
            }
            await using var admin = new AssetManagementTests.Session(connection: sourceConnection);
            await InitializeAccount(admin, "Admin", North);
            await admin.Login();
            await using var viewer = new AssetManagementTests.Session(connection: sourceConnection);
            await InitializeAccount(viewer, "Viewer", North);
            await viewer.Login();
            await using var foreign = new AssetManagementTests.Session(connection: sourceConnection);
            await InitializeAccount(foreign, "Viewer", South);
            await foreign.Login();
            async Task<DocumentDto> Upload(DocumentInput metadata, string text, Guid? id = null)
            {
                await Sample();
                using var form = new MultipartFormDataContent();
                form.Add(new StringContent(JsonSerializer.Serialize(metadata)), "metadata");
                var content = new ByteArrayContent(Encoding.UTF8.GetBytes(text));
                content.Headers.ContentType = new("text/markdown");
                form.Add(content, "file", "manual.md");
                var response = await admin.Client.PostAsync(id.HasValue ? $"/api/v1/documents/{id}/versions" : "/api/v1/documents", form);
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                await Sample();
                return (await response.Content.ReadFromJsonAsync<DocumentDto>())!;
            }
            async Task<DocumentDto> Publish(DocumentDto document, int version)
            {
                await Sample();
                var response = await admin.Client.PostAsJsonAsync($"/api/v1/documents/{document.Id}/publish", new PublishDocument(document.Version, version));
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                await Sample();
                return (await response.Content.ReadFromJsonAsync<DocumentDto>())!;
            }
            phase = "seed";
            var publicDoc = await Publish(await Upload(new(North, null, "Backup public manual"), "# Original public version\n" + new string('a', 65536)), 1);
            publicDoc = await Publish(await Upload(new(North, null, publicDoc.Title, Version: publicDoc.Version), "# Revised public version\n" + new string('b', 131072), publicDoc.Id), 2);
            var restricted = await Publish(await Upload(new(North, null, "Backup restricted manual", Visibility: "RestrictedRoles", AllowedRoles: ["Admin"]), "# Admin instructions\n" + new string('c', 65536)), 1);
            var sourceFiles = await Files(source, seedRoot);
            await admin.Factory.DisposeAsync();
            await viewer.Factory.DisposeAsync();
            await foreign.Factory.DisposeAsync();
            var beforeRows = await Digests(source);
            var beforeStructure = await Structure(source);
            await Save("source-tables.json", beforeRows);
            await Save("source-files.json", sourceFiles);
            await Save("source-structure.json", beforeStructure);
            Assert.Equal(await Scalar($"SELECT jsonb_build_object('owner',pg_get_userbyid(nspowner),'acl',nspacl)::text FROM pg_namespace WHERE nspname='{Accepted}'"), await Scalar($"SELECT jsonb_build_object('owner',pg_get_userbyid(nspowner),'acl',nspacl)::text FROM pg_namespace WHERE nspname='{source}'"));
            phase = "backup";
            await Sample();
            var dump = await Dump(source);
            await File.WriteAllTextAsync(Path.Combine(root, "database.sql"), dump, new UTF8Encoding(false));
            var dumpHash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(root, "database.sql"))));
            await Save("database-backup.json", new
            {
                Bytes = Encoding.UTF8.GetByteCount(dump),
                Sha256 = dumpHash,
                Frozen = true
            });
            foreach (var file in sourceFiles)
            {
                await using var output = new FileStream(Path.Combine(backupRoot, file.Key), FileMode.CreateNew);
                await using var input = File.OpenRead(Path.Combine(seedRoot, file.Key));
                await input.CopyToAsync(output);
                new FileInfo(Path.Combine(backupRoot, file.Key)).SetAccessControl(new FileInfo(Path.Combine(seedRoot, file.Key)).GetAccessControl());
                await Sample();
            }
            Assert.Equal(sourceFiles, await Files(source, backupRoot));
            phase = "restore";
            await Sample();
            var diskDump = await File.ReadAllTextAsync(Path.Combine(root, "database.sql"));
            Assert.Equal(dumpHash, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(diskDump))));
            var restoreSql = Remap(diskDump, source, target);
            await File.WriteAllTextAsync(Path.Combine(root, "restore.sql"), restoreSql, new UTF8Encoding(false));
            await Restore(await File.ReadAllTextAsync(Path.Combine(root, "restore.sql")));
            foreach (var file in sourceFiles)
            {
                await using var output = new FileStream(Path.Combine(restoredRoot, file.Key), FileMode.CreateNew);
                await using var input = File.OpenRead(Path.Combine(backupRoot, file.Key));
                await input.CopyToAsync(output);
                new FileInfo(Path.Combine(restoredRoot, file.Key)).SetAccessControl(new FileInfo(Path.Combine(backupRoot, file.Key)).GetAccessControl());
                await Sample();
            }
            phase = "verify";
            var restoredRows = await Digests(target);
            var restoredFiles = await Files(target, restoredRoot);
            Assert.Equal(beforeRows, restoredRows);
            Assert.Equal(beforeStructure, await Structure(target));
            Assert.Equal(await Scalar($"SELECT jsonb_build_object('owner',pg_get_userbyid(nspowner),'acl',nspacl)::text FROM pg_namespace WHERE nspname='{source}'"), await Scalar($"SELECT jsonb_build_object('owner',pg_get_userbyid(nspowner),'acl',nspacl)::text FROM pg_namespace WHERE nspname='{target}'"));
            Assert.Equal(sourceFiles, restoredFiles);
            Assert.Equal(beforeRows, await Digests(source));
            Assert.Equal(acceptedBefore, await Digests(Accepted));
            await Save("restored-tables.json", restoredRows);
            await Save("restored-files.json", restoredFiles);
            builder.SearchPath = target;
            Environment.SetEnvironmentVariable("PLANTOPS_TEST_DOCUMENT_ROOT", restoredRoot);
            async Task CheckAccess(AssetManagementTests.Session original, bool allowed)
            {
                await using var session = new AssetManagementTests.Session(connection: builder.ConnectionString);
                session.Client.Timeout = TimeSpan.FromSeconds(10);
                await session.RefreshToken();
                Assert.Equal(HttpStatusCode.NoContent, (await session.Client.PostAsJsonAsync("/api/v1/auth/login", new
                {
                    UserName = original.Name,
                    Password = original.Password
                })).StatusCode);
                await session.RefreshToken();
                foreach (var version in new[] { 1, 2 })
                {
                    var response = await session.Client.GetAsync($"/api/v1/documents/{publicDoc.Id}/versions/{version}/download");
                    Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.NotFound, response.StatusCode);
                    if (allowed)
                    {
                        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(seedRoot, sourceFiles.Single(x => x.Sha256 == (awaitMetadata(version))).Key)), await response.Content.ReadAsByteArrayAsync());
                    }
                }
                Assert.Equal(original == admin ? HttpStatusCode.OK : HttpStatusCode.NotFound, (await session.Client.GetAsync($"/api/v1/documents/{restricted.Id}/versions/1/download")).StatusCode);
                string awaitMetadata(int version) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(version == 1 ? "# Original public version\n" + new string('a', 65536) : "# Revised public version\n" + new string('b', 131072))));
            }
            await CheckAccess(admin, true);
            await CheckAccess(viewer, true);
            await CheckAccess(foreign, false);
            await using (var anonymous = new AssetManagementTests.Session(connection: builder.ConnectionString))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.Client.GetAsync($"/api/v1/documents/{publicDoc.Id}/versions/1/download")).StatusCode);
            }
            await Sample();
            await Save("result.json", new
            {
                Passed = true,
                CapacityPassed = false,
                Source = source,
                Restored = target,
                RawRows = 12000,
                Documents = 2,
                Files = 3,
                FileBytes = sourceFiles.Sum(x => x.Bytes),
                DumpBytes = new FileInfo(Path.Combine(root, "database.sql")).Length,
                Seconds = timer.Elapsed.TotalSeconds,
                Tables = beforeRows.Count,
                Backup = Summary("backup"),
                Restore = Summary("restore"),
                All = Summary(null),
                Note = "Quiescent logical backup. Shared cluster/host sampled observations, not instantaneous or full-capacity peaks. Accepted source unchanged. No continuous adapter."
            });
            object Summary(string? name)
            {
                var samples = probes.Where(x => name == null || x.Phase == name).ToArray();
                var first = samples[0];
                return new
                {
                    Samples = samples.Length,
                    DurationSeconds = samples[^1].Seconds - first.Seconds,
                    Baseline = first,
                    Last = samples[^1],
                    CObservedGrowth = Math.Max(0, first.CFree - samples.Min(x => x.CFree)),
                    DObservedGrowth = Math.Max(0, first.DFree - samples.Min(x => x.DFree)),
                    WalOccupancyPeak = samples.Max(x => x.WalBytes),
                    WalOccupancyGrowth = Math.Max(0, samples.Max(x => x.WalBytes) - first.WalBytes),
                    WalGenerated = samples[^1].WalGenerated - first.WalGenerated,
                    ArtifactPeakBytes = samples.Max(x => x.ArtifactBytes),
                    ArtifactGrowthBytes = samples.Max(x => x.ArtifactBytes) - first.ArtifactBytes
                };
            }
        }
        catch (Exception error)
        {
            await Save("failure.json", new
            {
                Phase = phase,
                Type = error.GetType().Name,
                Seconds = timer.Elapsed.TotalSeconds,
                CapacityPassed = false
            });
            throw;
        }
    }
}
