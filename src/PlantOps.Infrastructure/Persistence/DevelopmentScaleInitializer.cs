using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PlantOps.Application.Knowledge;
using PlantOps.Domain.Assets;
using PlantOps.Domain.Telemetry;
using PlantOps.Domain.Knowledge;
using PlantOps.Domain.Operations;
using PlantOps.Infrastructure.Telemetry;

namespace PlantOps.Infrastructure.Persistence;

public static class DevelopmentScaleInitializer
{
    private static Guid Id(int family, int n) => Guid.Parse($"{family:D8}-0000-0000-0000-{n:D12}");

    public static async Task InitializeAsync(IServiceProvider services, IConfiguration config)
    {
        var db = services.GetRequiredService<PlantOpsDbContext>();
        var files = services.GetRequiredService<IDocumentFileStore>();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7300103)");
        await MeasurementMaintenance.LockAsync(db, default);
        var assets = new List<Asset>();
        var pointIds = new List<Guid>();
        for (var plantIndex = 0; plantIndex < 2; plantIndex++)
        {
            var plant = Id(10000000, plantIndex + 1);
            var root = Id(20000000, plantIndex + 1);
            for (var l = 0; l < 2; l++)
            {
                var id = Id(21000000, plantIndex * 2 + l + 1);
                if (!await db.Locations.AnyAsync(x => x.Id == id))
                {
                    db.Locations.Add(new Location { Id = id, PlantId = plant, ParentLocationId = root, Code = l == 0 ? "EQUIPMENT_ROOM" : "WORKSHOP", Name = l == 0 ? "Equipment Room" : "Workshop", SortOrder = l + 1 });
                }
            }
            var source = Id(50000000, plantIndex + 1);
            if (!await db.DataSources.AnyAsync(x => x.Id == source))
            {
                db.DataSources.Add(new DataSource { Id = source, PlantId = plant, Name = "South deterministic simulator" });
            }

            for (var a = 0; a < 10; a++)
            {
                var index = plantIndex * 10 + a;
                var aid = a == 0 ? Id(30000000, plantIndex + 1) : Id(31000000, index + 1);
                var asset = await db.Assets.SingleOrDefaultAsync(x => x.Id == aid);
                if (asset == null)
                {
                    asset = new Asset { Id = aid, PlantId = plant, LocationId = a % 3 == 0 ? root : Id(21000000, plantIndex * 2 + (a % 3)), Code = $"{(plantIndex == 0 ? "N" : "S")}-{a + 101}", Name = $"Fictional {(a % 2 == 0 ? "Pump" : "Fan")} {a + 101}", AssetType = a % 2 == 0 ? "Pump" : "Fan" };
                    db.Assets.Add(asset);
                }
                assets.Add(asset);
                for (var p = 0; p < 5; p++)
                {
                    var pid = plantIndex == 0 && a == 0 && p == 0 ? Id(40000000, 1) : Id(41000000, index * 5 + p + 1);
                    pointIds.Add(pid);
                    if (!await db.MeasurementPoints.AnyAsync(x => x.Id == pid))
                    {
                        db.MeasurementPoints.Add(new MeasurementPoint { Id = pid, PlantId = plant, AssetId = aid, Code = new[] { "VIBRATION_RMS", "VIBRATION_AXIAL", "VIBRATION_RADIAL", "VIBRATION_MOTOR", "VIBRATION_COUPLING" }[p], Name = new[] { "Vibration RMS", "Axial vibration", "Radial vibration", "Motor vibration", "Coupling vibration" }[p] });
                    }

                    var mid = plantIndex == 0 && a == 0 && p == 0 ? Id(60000000, 1) : Id(61000000, index * 5 + p + 1);
                    if (!await db.SourceMappings.AnyAsync(x => x.Id == mid))
                    {
                        db.SourceMappings.Add(new SourceMapping { Id = mid, PlantId = plant, DataSourceId = source, PointId = pid, SourceKey = $"{asset.Code}.Vibration{p}" });
                    }
                }
            }
        }
        await db.SaveChangesAsync();
        for (var d = 0; d < 10; d++)
        {
            var did = Id(81000000, d + 1);
            if (await db.Documents.AnyAsync(x => x.Id == did))
            {
                continue;
            }

            var asset = assets[d * 2];
            var content = Encoding.UTF8.GetBytes($"# Original fictional {asset.Code} maintenance guide\n\nThis document describes fictional demonstration equipment only.\n\n1. Follow the site isolation procedure before inspection.\n2. Inspect coupling alignment, mounting bolts and bearing condition.\n3. Record vibration in mm/s and compare before/after measurements.\n4. Write the maintenance result before completing the work order.\n\nReference: Golden Path training guide {d + 1}. No real plant data.\n");
            var v = new DocumentVersion { DocumentId = did, VersionNumber = 1, FileName = $"guide-{d + 1}.md", MediaType = "text/markdown", StorageKey = await files.PutAsync(content), SizeBytes = content.Length, Sha256 = Convert.ToHexStringLower(SHA256.HashData(content)), UploadedByUserId = "development:seed", UploadedAt = DateTimeOffset.UtcNow, PublishedAt = DateTimeOffset.UtcNow, Status = "Published" };
            db.Documents.Add(new Document { Id = did, PlantId = asset.PlantId, AssetId = asset.Id, Title = $"Original {asset.Code} maintenance guide", CurrentPublishedVersionId = v.Id, Visibility = d % 3 == 0 ? "RestrictedRoles" : "PlantReaders", AllowedRoles = d % 3 == 0 ? ["Admin", "MaintenanceEngineer"] : [] });
            db.DocumentVersions.Add(v);
        }
        await db.SaveChangesAsync();
        if (config.GetValue("Demo:SeedHistory", true) && !await db.DevelopmentSeedStates.AnyAsync(x => x.Name == "p1-one-hour-history"))
        {
            var end = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            var start = end.AddHours(-1);
            var dates = new[] { DateOnly.FromDateTime(start.UtcDateTime), DateOnly.FromDateTime(end.AddSeconds(-1).UtcDateTime) }.Distinct().ToArray();
            var partitions = await MeasurementMaintenance.EnsureAsync(db, dates, default);
            foreach (var partition in partitions.Values)
            {
                partition.Revision++;
            }

            await db.SaveChangesAsync();
            // Server-side bulk development history; the same 120-second scenario as SimulationSource. Existing latest states and edits are preserved.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "MeasurementReadings" ("Id","EffectiveDate","PlantId","AssetId","PointId","NumericValue","CanonicalUnit","RawValueJson","RawUnit","SourceKind","DataSourceId","SourceMappingId","MappingVersion","Quality","SourceTimestamp","ServerTimestamp","ReceivedAt","EffectiveAt","TimestampOrigin","SourceEventKey","TimeAnomaly")
                SELECT gen_random_uuid(), (t.at AT TIME ZONE 'UTC')::date, p."PlantId",p."AssetId",p."Id",
                  CASE WHEN mod(extract(epoch FROM t.at)::bigint,120) BETWEEN 60 AND 69 THEN NULL WHEN mod(extract(epoch FROM t.at)::bigint,120)<30 THEN 4.0 WHEN mod(extract(epoch FROM t.at)::bigint,120)<60 THEN 12.6 ELSE 6.0 END,
                  p."CanonicalUnit",'null'::jsonb,m."RawUnit",'Simulation',m."DataSourceId",m."Id",m."Version",
                  CASE WHEN mod(extract(epoch FROM t.at)::bigint,120) BETWEEN 60 AND 69 THEN 'Bad' WHEN mod(extract(epoch FROM t.at)::bigint,120) BETWEEN 70 AND 79 THEN 'Uncertain' ELSE 'Good' END,
                  t.at,t.at,t.at,t.at,'Source','development:' || m."Id" || ':' || extract(epoch FROM t.at)::bigint,false
                FROM "MeasurementPoints" p JOIN "SourceMappings" m ON m."PointId"=p."Id" AND m."Enabled"
                CROSS JOIN generate_series({start}, {end.AddSeconds(-1)},interval '1 second') t(at)
                WHERE p."Id" = ANY ({pointIds.ToArray()}) AND NOT EXISTS (SELECT 1 FROM "PointLatestStates" s WHERE s."PointId"=p."Id")
                """);
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO "DirtyMeasurementMinutes" SELECT DISTINCT "PointId",date_trunc('minute',"EffectiveAt",'UTC') FROM "MeasurementReadings" ON CONFLICT DO NOTHING;
                WITH latest AS (SELECT DISTINCT ON ("PointId") * FROM "MeasurementReadings" ORDER BY "PointId","EffectiveAt" DESC),
                  good AS (SELECT DISTINCT ON ("PointId") * FROM "MeasurementReadings" WHERE "Quality"='Good' AND NOT "TimeAnomaly" ORDER BY "PointId","EffectiveAt" DESC)
                INSERT INTO "PointLatestStates" ("PointId","LatestObservedReadingId","LatestObservedValue","LatestObservedQuality","LatestObservedEffectiveAt","LatestObservedReceivedAt","LastGoodReadingId","LastGoodValue","LastTrustedEffectiveAt","LastGoodReceivedAt","LastReceivedAt","Version")
                SELECT l."PointId",l."Id",l."NumericValue",l."Quality",l."EffectiveAt",l."ReceivedAt",g."Id",g."NumericValue",g."EffectiveAt",g."ReceivedAt",l."ReceivedAt",1 FROM latest l LEFT JOIN good g ON g."PointId"=l."PointId" ON CONFLICT DO NOTHING;
                """);
            db.DevelopmentSeedStates.Add(new DevelopmentSeedState { Name = "p1-one-hour-history", SeededAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        await tx.CommitAsync();
    }
}
