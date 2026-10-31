using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PlantOps.Application.AI;

namespace PlantOps.Infrastructure.AI;

// Dedicated operator-only storage. No download route or public document-store link.
public sealed class InvestigationResponseStore(IConfiguration configuration, TimeProvider clock,
    ILogger<InvestigationResponseStore> logger) : IInvestigationResponseStore, IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private bool Enabled => configuration.GetValue("AI:Diagnostics:CaptureResponses", true);

    private int RetentionDays => Math.Clamp(configuration.GetValue("AI:Diagnostics:RetentionDays", 7), 1, 30);

    private int MaxCaptureBytes => Math.Clamp(configuration.GetValue("AI:Diagnostics:MaxCaptureBytes", 262144), 1024, 1048576);

    private long MaxTotalBytes => Math.Clamp(configuration.GetValue("AI:Diagnostics:MaxTotalBytes", 104857600L), 1048576, 1073741824);

    private int MaxFiles => Math.Clamp(configuration.GetValue("AI:Diagnostics:MaxFiles", 1000), 10, 10000);

    private string Root()
    {
        var root = configuration["AI:Diagnostics:StorageRoot"] ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PlantOps", "investigation-diagnostics");
        if (!Path.IsPathFullyQualified(root))
        {
            throw new IOException("Diagnostic root must be absolute.");
        }
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(root);
        }
        else
        {
            Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Diagnostic root must not be a link.");
        }
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return root;
    }

    public async Task<InvestigationResponseReceipt> SaveAsync(InvestigationResponseCapture capture, CancellationToken cancellationToken)
    {
        if (!Enabled)
        {
            return new("Disabled");
        }
        await gate.WaitAsync(cancellationToken);
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                version = "investigation-response-v1",
                capture
            }, Json);
            if (bytes.Length > MaxCaptureBytes)
            {
                logger.LogWarning("Investigation response capture omitted: runId={RunId}; reason=SizeLimit; bytes={Bytes}", capture.RunId, bytes.Length);
                return new("SizeLimit");
            }
            var root = Root();
            var id = $"response-{capture.RunId:N}-{capture.LeaseId:N}-{Guid.NewGuid():N}.json";
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }
            await using (var file = new FileStream(Path.Combine(root, id), options))
            {
                await file.WriteAsync(bytes, cancellationToken);
                await file.FlushAsync(cancellationToken);
            }
            Prune(root);
            return new("Stored", id);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("Investigation response capture failed: runId={RunId}; errorType={ErrorType}", capture.RunId, exception.GetType().Name);
            return new("WriteFailed");
        }
        finally
        {
            gate.Release();
        }
    }

    // Prune even when capture is disabled, so disabling capture does not retain old payloads forever.
    public async Task PruneAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            Prune(Root());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("Investigation response retention deferred: errorType={ErrorType}", exception.GetType().Name);
        }
        finally
        {
            gate.Release();
        }
    }

    private void Prune(string root)
    {
        var files = new DirectoryInfo(root).GetFiles("response-*.json")
            .Where(x => (x.Attributes & FileAttributes.ReparsePoint) == 0)
            .OrderBy(x => x.LastWriteTimeUtc).ToArray();
        var total = files.Sum(x => x.Length);
        var cutoff = clock.GetUtcNow().UtcDateTime.AddDays(-RetentionDays);
        var deleted = 0;
        foreach (var file in files)
        {
            if (file.LastWriteTimeUtc >= cutoff && total <= MaxTotalBytes && files.Length - deleted <= MaxFiles)
            {
                continue;
            }
            var length = file.Length;
            file.Delete(); // Enumerated direct children only; never recursive or a caller-supplied filename.
            total -= length;
            deleted++;
        }
        if (deleted > 0)
        {
            logger.LogInformation("Investigation response retention: deleted={Deleted}; remainingBytes={Bytes}", deleted, total);
        }
    }

    public void Dispose() => gate.Dispose();
}
