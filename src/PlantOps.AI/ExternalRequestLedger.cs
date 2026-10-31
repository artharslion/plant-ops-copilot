using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PlantOps.Application.AI;

namespace PlantOps.AI;

// Optional acceptance-run cap shared by processes. An existing, explicitly initialized
// ledger is required; corrupt, missing or locked ledgers fail closed before HTTP.
internal static class ExternalRequestLedger
{
    public static void RecordCompletion(byte[] body, LlmCompletion completion)
    {
        var path = Environment.GetEnvironmentVariable("PLANTOPS_LLM_REQUEST_LEDGER");
        if (string.IsNullOrEmpty(path))
        {
            return;
        }
        var target = path + ".completion-" + Guid.NewGuid().ToString("N") + ".json";
        using var file = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(file, new
        {
            requestSha256 = Convert.ToHexString(SHA256.HashData(body)),
            receivedAt = DateTimeOffset.UtcNow,
            completion
        });
        file.Flush(flushToDisk: true);
    }

    public static void Reserve(string model, byte[] body)
    {
        var path = Environment.GetEnvironmentVariable("PLANTOPS_LLM_REQUEST_LEDGER");
        if (string.IsNullOrEmpty(path))
        {
            return;
        }
        ReserveAt(path, model, body);
        if (Environment.GetEnvironmentVariable("PLANTOPS_LLM_CAPTURE_REQUESTS") == "1")
        {
            // Acceptance-only payload capture. Authorization headers are never part
            // of this serialized request body; callers opt in for fictional fixtures.
            using var capture = new FileStream(path + ".request-" + Guid.NewGuid().ToString("N") + ".json",
                FileMode.CreateNew, FileAccess.Write, FileShare.None);
            capture.Write(body);
            capture.Flush(flushToDisk: true);
        }
    }

    internal static void ReserveAt(string path, string model, byte[] body)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            using var reader = new StreamReader(file, Encoding.UTF8, leaveOpen: true);
            var content = reader.ReadToEnd();
            if (!content.EndsWith('\n'))
            {
                throw new InvalidDataException();
            }
            var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            using var header = JsonDocument.Parse(lines[0]);
            var limit = header.RootElement.GetProperty("limit").GetInt32();
            if (limit is < 1 or > 100 || header.RootElement.GetProperty("kind").GetString() != "authorization")
            {
                throw new InvalidDataException();
            }
            for (var i = 1; i < lines.Length; i++)
            {
                using var entry = JsonDocument.Parse(lines[i]);
                if (entry.RootElement.GetProperty("sequence").GetInt32() != i)
                {
                    throw new InvalidDataException();
                }
            }
            if (lines.Length - 1 >= limit)
            {
                throw new LlmException(LlmFailure.BudgetExceeded);
            }
            file.Position = file.Length;
            var record = JsonSerializer.Serialize(new
            {
                sequence = lines.Length,
                reservedAt = DateTimeOffset.UtcNow,
                model,
                requestSha256 = Convert.ToHexString(SHA256.HashData(body)),
                processId = Environment.ProcessId,
                status = "reserved-before-send-usage-unknown"
            });
            var bytes = Encoding.UTF8.GetBytes(record + "\n");
            file.Write(bytes);
            file.Flush(flushToDisk: true);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or InvalidOperationException or IndexOutOfRangeException or KeyNotFoundException)
        {
            throw new LlmException(LlmFailure.BudgetExceeded);
        }
    }
}
