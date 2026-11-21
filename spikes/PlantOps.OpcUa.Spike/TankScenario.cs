using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PlantOps.Application.AI;
using PlantOps.Application.Assets;
using PlantOps.Domain.Assets;
using PlantOps.Infrastructure.Persistence;

namespace PlantOps.OpcUa.Spike;

internal sealed class TankScenario : IProtocolScenario
{
    private readonly string variant;
    private DateTimeOffset start;
    private readonly Stopwatch elapsed = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public ScenarioPoint[] Definitions
    {
        get;
    }

    public TankScenario(string variant)
    {
        this.variant = variant is "filling" or "missing" or "idle" ? variant : throw new ArgumentException("Unknown tank case.");
        ScenarioPoint[] points = [
            new("Tank.AutomaticLevel", "AUTO_LEVEL", "Level", "m", 5067858),
            new("Tank.InletFlow", "INLET_FLOW", "VolumeFlow", "m3/h", 5067080),
            new("Tank.OutletFlow", "OUTLET_FLOW", "VolumeFlow", "m3/h", 5067080)
        ];
        Definitions = variant == "missing" ? [.. points, new("Tank.ManualGaugeLog", "MANUAL_SPOT_LEVEL", "Level", "m", 5067858)] : points;
    }

    public ScenarioObservation Generate(int point)
    {
        if (!elapsed.IsRunning)
        {
            var now = DateTimeOffset.UtcNow;
            start = new DateTimeOffset(now.Ticks / 10 * 10, TimeSpan.Zero);
            elapsed.Start();
        }
        var second = (int)elapsed.Elapsed.TotalSeconds;
        // A common historical alarm opens all three investigations. The comparison window starts at t=18.
        var sampleSecond = point switch
        {
            0 when variant == "missing" => Math.Min(second, 18),
            3 => second < 24 ? 0 : 24,
            _ => second
        };
        var value = point switch
        {
            0 => sampleSecond < 12 ? 5.20 : 5.00,
            1 => second >= 18 && variant != "idle" ? 360.0 : 0,
            2 => 0,
            3 => sampleSecond == 24 ? 5.06 : 5.20,
            _ => throw new ArgumentOutOfRangeException(nameof(point))
        };
        return new(Definitions[point].Identifier, sampleSecond, value, start.AddSeconds(sampleSecond), false);
    }

    public void ConfigureInvestigation(IConfigurationBuilder builder)
    {
        var real = Environment.GetEnvironmentVariable("PLANTOPS_TANK_REAL") == "1";
        if (real && !File.Exists(Environment.GetEnvironmentVariable("PLANTOPS_LLM_REQUEST_LEDGER")))
        {
            throw new InvalidOperationException("An authorization ledger is required.");
        }

        var config = new Dictionary<string, string?>();
        if (real)
        {
            foreach (var key in new[] { "BaseUrl", "ApiKey", "Model", "ReasoningEffort" })
            {
                config["AI:" + key] = Environment.GetEnvironmentVariable("AI__" + key);
            }
        }
        config["AI:Provider"] = real ? "OpenAiCompatible" : "Fake";
        config["AI:MaxRetries"] = "0";
        config["AI:MaxInputTokens"] = "32000";
        config["AI:MaxOutputTokens"] = "6000";
        config["AI:StreamResponses"] = "true";
        builder.AddInMemoryCollection(config);
    }

    public async Task<Guid> CreateAssetAsync(PlantOpsDbContext db, Guid templateId, CancellationToken ct)
    {
        var template = await db.Assets.AsNoTracking().SingleAsync(x => x.Id == templateId, ct);
        var tank = new Asset
        {
            Id = Guid.NewGuid(),
            PlantId = template.PlantId,
            LocationId = template.LocationId,
            Code = "T-01",
            AssetType = "Tank",
            Name = "计量储罐（截面积 10 m²，唯一进出口均计量，液位分辨率 0.01 m）"
        };
        db.Assets.Add(tank);
        return tank.Id;
    }

    public async Task ReviewAsync(IServiceProvider provider, string root, Guid[] ids, CancellationToken ct)
    {
        while (elapsed.Elapsed.TotalSeconds < 35)
        {
            await Task.Delay(250, ct);
        }

        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlantOpsDbContext>();
        var rows = await db.MeasurementReadings.AsNoTracking().Where(r => ids.Contains(r.PointId))
            .OrderBy(r => r.EffectiveAt).ToArrayAsync(ct);
        var window = rows.Where(r => r.EffectiveAt >= start.AddSeconds(18)).ToArray();
        var capturedAt = DateTimeOffset.UtcNow;
        var source = rows.FirstOrDefault()?.DataSourceId;
        var connected = await db.DataSourceHealth.AnyAsync(h => h.DataSourceId == source && h.State == "Connected", ct);
        var valid = connected && Definitions.Select((p, i) =>
        {
            var samples = window.Where(r => r.PointId == ids[i]).ToArray();
            if (samples.Length == 0 || samples.Any(r => r.Quality != "Good" || r.CanonicalUnit != p.Unit))
            {
                return false;
            }

            var age = capturedAt - samples[^1].EffectiveAt;
            return i switch
            {
                0 when variant == "missing" => age.TotalSeconds > 5 && samples[^1].NumericValue == 5,
                3 => samples.Select(r => r.SourceTimestamp).Distinct().Count() == 1 && samples[^1].NumericValue == 5.06,
                _ => samples.Length >= 8 && age.TotalSeconds < 5 && samples.All(r => r.NumericValue ==
                    (i == 0 ? 5.0 : i == 1 && variant != "idle" ? 360.0 : 0))
            };
        }).All(v => v);
        await File.WriteAllTextAsync(Path.Combine(root, "protocol.json"), JsonSerializer.Serialize(new
        {
            variant,
            start,
            capturedAt,
            connected,
            valid,
            Definitions,
            ids,
            rows
        }, Json), ct);
        if (!valid)
        {
            throw new InvalidOperationException("Tank protocol evidence mismatch.");
        }

        var alarm = await db.Alarms.SingleAsync(a => a.PointId == ids[0] && a.State == "Recovered", ct);
        var actor = new Actor(await db.Users.Where(u => u.UserName == "operator").Select(u => u.Id).SingleAsync(ct), Guid.NewGuid().ToString());
        var service = scope.ServiceProvider.GetRequiredService<IInvestigationService>();
        var run = await service.CreateAsync(actor, alarm.Id, ct);
        await service.ProcessNextAsync(ct);
        run = await service.GetAsync(actor, run.Id, ct);
        await File.WriteAllTextAsync(Path.Combine(root, "investigation.json"), JsonSerializer.Serialize(run, Json), ct);
        Console.WriteLine($"Tank protocol verified. Investigation {run.Id}: {run.Status} {run.FailureCode}");
    }
}
