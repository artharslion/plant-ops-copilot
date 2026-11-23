using System.Diagnostics;
using System.Text.Json;

namespace PlantOps.OpcUa.Spike;

// Owns only the finite example process it starts, never existing simulator services.
internal sealed class IndustrialSimProcess(string simulatorRoot, string evidenceRoot, string? scenarioPath = null) : IAsyncDisposable
{
    public const string Endpoint = "opc.tcp://127.0.0.1:48481";
    public const string NamespaceUri = "urn:industrial-sim:runtime";
    public static readonly string[] Nodes = ["plantops-p101/temperature", "plantops-p101/referenceTemperature", "plantops-p101/vibration"];

    private Process? process;
    private Task<string>? stdout;
    private Task<string>? stderr;

    public DateTimeOffset StartedAt
    {
        get; private set;
    }

    public double ElapsedSeconds => (DateTimeOffset.UtcNow - StartedAt).TotalSeconds;

    public bool CompletedSuccessfully
    {
        get; private set;
    }

    public void EnsureRunning()
    {
        if (process?.HasExited == true)
        {
            throw new IOException("IndustrialSim exited; inspect retained simulator evidence and logs.");
        }
    }

    public async Task StartAsync(CancellationToken ct)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = simulatorRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[]
        {
            Path.Combine(simulatorRoot, "examples/PlantOps.Scenario/bin/Debug/net10.0/PlantOps.Scenario.dll"),
            Path.Combine(simulatorRoot, "examples/devices/plantops-three-point.yaml"),
            scenarioPath ?? Path.Combine(simulatorRoot, "examples/scenarios/plantops-three-point.yaml"), evidenceRoot
        })
        {
            start.ArgumentList.Add(argument);
        }
        process = Process.Start(start) ?? throw new IOException("Simulator process did not start.");
        // Drain through shutdown even when the experiment deadline has expired.
        stdout = process.StandardOutput.ReadToEndAsync();
        stderr = process.StandardError.ReadToEndAsync();
        using var readyDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        readyDeadline.CancelAfter(TimeSpan.FromSeconds(20));
        var ready = Path.Combine(evidenceRoot, "ready.json");
        while (!File.Exists(ready))
        {
            if (process.HasExited)
            {
                throw new IOException("IndustrialSim exited before readiness; inspect retained logs.");
            }
            await Task.Delay(100, readyDeadline.Token);
        }
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(ready, readyDeadline.Token));
        StartedAt = manifest.RootElement.GetProperty("StartedAt").GetDateTimeOffset();
    }

    public async ValueTask DisposeAsync()
    {
        if (process == null)
        {
            return;
        }
        try
        {
            if (!process.HasExited)
            {
                try
                {
                    await process.StandardInput.WriteLineAsync("quit");
                }
                catch (IOException) { /* The owned process may have closed stdin during exit. */ }
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                try
                {
                    await process.WaitForExitAsync(stop.Token);
                }
                catch (OperationCanceledException)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
            Directory.CreateDirectory(evidenceRoot);
            await File.WriteAllTextAsync(Path.Combine(evidenceRoot, "stdout.log"), await stdout!);
            await File.WriteAllTextAsync(Path.Combine(evidenceRoot, "stderr.log"), await stderr!);
            CompletedSuccessfully = process.ExitCode == 0 && File.Exists(Path.Combine(evidenceRoot, "simulator-result.json"));
        }
        finally
        {
            process.Dispose();
            process = null;
        }
    }
}
