using System.Text;
using System.Text.Json;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PlantOps.Application.AI;

namespace PlantOps.AI;

internal sealed class SseCompletionReader
{
    private readonly StringBuilder content = new();
    private readonly StringBuilder refusal = new();
    private readonly SortedDictionary<int, (StringBuilder Id, StringBuilder Name, StringBuilder Arguments)> calls = [];
    private string? model;
    private string? finish;
    private JsonElement? usage;
    private bool done;
    private string phase = "event-json";
    private int events;
    private int payloadBytes;
    private int maximumPayloadBytes;

    public static async Task<LlmCompletion> ReadAsync(HttpContent http, int maximumBytes, LlmRequest request, CancellationToken ct,
        ILogger? logger = null, Action? onActivity = null, LlmBudget? budget = null)
    {
        var reader = new SseCompletionReader { maximumPayloadBytes = maximumBytes };
        // SSE repeats metadata for every token. Bound that framing separately from the assembled response.
        var maximumWireBytes = checked(maximumBytes * 8);
        await using var stream = await http.ReadAsStreamAsync(ct);
        using var line = new MemoryStream();
        var data = new StringBuilder();
        var buffer = new byte[4096];
        var total = 0;
        var elapsed = Stopwatch.StartNew();
        long lastActivityMs = 0;
        long lastLogMs = 0;
        try
        {
            reader.phase = "content-type";
            if (http.Headers.ContentType?.MediaType != "text/event-stream")
            {
                throw new LlmException(LlmFailure.InvalidResponse);
            }
            reader.phase = "wire-size-limit";
            if (http.Headers.ContentLength > maximumWireBytes)
            {
                throw new LlmException(LlmFailure.InvalidResponse);
            }
            while (!reader.done)
            {
                reader.phase = "read";
                var read = await stream.ReadAsync(buffer, ct);
                if (read == 0)
                {
                    reader.phase = "missing-done";
                    throw new LlmException(LlmFailure.InvalidResponse);
                }
                if (read > maximumWireBytes - total)
                {
                    reader.phase = "wire-size-limit";
                    throw new LlmException(LlmFailure.InvalidResponse);
                }
                total += read;
                // Transport liveness is separate from a completed, validated answer.
                // Even comment/heartbeat bytes renew the idle timer, never the run deadline.
                onActivity?.Invoke();
                lastActivityMs = elapsed.ElapsedMilliseconds;
                if (total == read || lastActivityMs - lastLogMs >= 15000)
                {
                    logger?.LogInformation("AI stream activity: stage=receiving; elapsedMs={ElapsedMs}; bytes={Bytes}; events={Events}; payloadBytes={PayloadBytes}",
                        lastActivityMs, total, reader.events, reader.payloadBytes);
                    lastLogMs = lastActivityMs;
                }
                for (var i = 0; i < read && !reader.done; i++)
                {
                    if (buffer[i] != (byte)'\n')
                    {
                        line.WriteByte(buffer[i]);
                        continue;
                    }
                    reader.phase = "utf8";
                    var text = new UTF8Encoding(false, true).GetString(line.ToArray()).TrimEnd('\r');
                    line.SetLength(0);
                    if (text.Length == 0)
                    {
                        if (data.Length > 0)
                        {
                            reader.Accept(data.ToString());
                            data.Clear();
                        }
                    }
                    else if (text.StartsWith("data:", StringComparison.Ordinal))
                    {
                        if (data.Length > 0)
                        {
                            data.Append('\n');
                        }
                        data.Append(text.AsSpan(5).TrimStart(' '));
                    }
                }
            }
            reader.phase = "missing-finish-or-model";
            if (reader.finish == null || reader.model == null)
            {
                throw new LlmException(LlmFailure.InvalidResponse);
            }
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                model = reader.model,
                choices = new[]
                {
                    new
                    {
                        finish_reason = reader.finish,
                        message = new
                        {
                            role = "assistant",
                            content = reader.content.ToString(),
                            refusal = reader.refusal.ToString(),
                            tool_calls = reader.calls.Values.Select(call => new
                            {
                                id = call.Id.ToString(), type = "function",
                                function = new { name = call.Name.ToString(), arguments = call.Arguments.ToString() }
                            }).ToArray()
                        }
                    }
                },
                usage = reader.usage
            });
            reader.phase = "assembled-size-limit";
            if (bytes.Length > maximumBytes)
            {
                throw new LlmException(LlmFailure.InvalidResponse);
            }
            reader.phase = "completion-validation";
            var completion = ChatCompletionsProtocol.Parse(bytes, request);
            budget?.RecordDiagnostic("stream", "Validated", new
            {
                phase = reader.phase,
                elapsedMs = elapsed.ElapsedMilliseconds,
                wireBytes = total,
                reader.events,
                reader.payloadBytes,
                hasUsage = reader.usage.HasValue
            });
            logger?.LogInformation("AI stream activity: stage=validated; elapsedMs={ElapsedMs}; bytes={Bytes}; events={Events}; payloadBytes={PayloadBytes}; usage={HasUsage}",
                elapsed.ElapsedMilliseconds, total, reader.events, reader.payloadBytes, reader.usage.HasValue);
            return completion;
        }
        catch (OperationCanceledException)
        {
            budget?.RecordDiagnostic("stream", "CancelledOrTimedOut", new
            {
                phase = reader.phase,
                elapsedMs = elapsed.ElapsedMilliseconds,
                idleMs = elapsed.ElapsedMilliseconds - lastActivityMs,
                wireBytes = total,
                reader.events,
                reader.payloadBytes
            });
            logger?.LogWarning("AI stream stopped: stage={Stage}; elapsedMs={ElapsedMs}; idleMs={IdleMs}; bytes={Bytes}; events={Events}; payloadBytes={PayloadBytes}",
                reader.phase, elapsed.ElapsedMilliseconds, elapsed.ElapsedMilliseconds - lastActivityMs, total, reader.events, reader.payloadBytes);
            throw;
        }
        catch (LlmException exception)
        {
            budget?.RecordDiagnostic("stream", "Rejected", new
            {
                phase = reader.phase,
                failureCode = exception.Failure.ToString(),
                wireBytes = total,
                reader.events,
                reader.payloadBytes,
                hasFinish = reader.finish != null,
                hasUsage = reader.usage.HasValue
            });
            // Only fixed stage names and counters; never log upstream content, identifiers or exception bodies.
            logger?.LogWarning("AI stream failed: {Failure}; stage={Stage}; bytes={Bytes}; events={Events}; finish={HasFinish}; usage={HasUsage}",
                exception.Failure, reader.phase, total, reader.events, reader.finish != null, reader.usage.HasValue);
            throw;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException
            or FormatException or OverflowException or ArgumentException)
        {
            budget?.RecordDiagnostic("stream", "Invalid", new
            {
                phase = reader.phase,
                wireBytes = total,
                reader.events,
                reader.payloadBytes,
                hasFinish = reader.finish != null,
                hasUsage = reader.usage.HasValue
            });
            logger?.LogWarning("AI stream invalid: stage={Stage}; bytes={Bytes}; events={Events}; finish={HasFinish}; usage={HasUsage}",
                reader.phase, total, reader.events, reader.finish != null, reader.usage.HasValue);
            throw new LlmException(LlmFailure.InvalidResponse);
        }
    }

    private void Accept(string data)
    {
        events++;
        phase = "event-json";
        if (data == "[DONE]")
        {
            done = true;
            return;
        }
        using var document = JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        phase = "model";
        var nextModel = root.GetProperty("model").GetString();
        if (string.IsNullOrWhiteSpace(nextModel) || model != null && model != nextModel)
        {
            throw new LlmException(LlmFailure.InvalidResponse);
        }
        model = nextModel;
        phase = "usage";
        if (root.TryGetProperty("usage", out var reported) && reported.ValueKind != JsonValueKind.Null)
        {
            if (usage.HasValue)
            {
                throw new LlmException(LlmFailure.InvalidResponse);
            }
            usage = reported.Clone();
        }
        phase = "choices";
        var choices = root.GetProperty("choices");
        if (choices.GetArrayLength() > 1)
        {
            throw new LlmException(LlmFailure.InvalidResponse);
        }
        foreach (var choice in choices.EnumerateArray())
        {
            phase = "choice-index-or-after-finish";
            if (choice.GetProperty("index").GetInt32() != 0 || finish != null)
            {
                throw new LlmException(LlmFailure.InvalidResponse);
            }
            phase = "delta";
            var delta = choice.GetProperty("delta");
            if (delta.TryGetProperty("role", out var role) && role.GetString() != "assistant")
            {
                throw new LlmException(LlmFailure.InvalidResponse);
            }
            Append(delta, "content", content);
            Append(delta, "refusal", refusal);
            if (delta.TryGetProperty("tool_calls", out var tools))
            {
                phase = "tool-delta";
                foreach (var tool in tools.EnumerateArray())
                {
                    var index = tool.GetProperty("index").GetInt32();
                    if (index is < 0 or > 7 || tool.TryGetProperty("type", out var type) && type.GetString() != "function")
                    {
                        throw new LlmException(LlmFailure.InvalidResponse);
                    }
                    if (!calls.TryGetValue(index, out var call))
                    {
                        call = (new(), new(), new());
                        calls.Add(index, call);
                    }
                    Append(tool, "id", call.Id);
                    if (tool.TryGetProperty("function", out var function))
                    {
                        Append(function, "name", call.Name);
                        Append(function, "arguments", call.Arguments);
                    }
                }
            }
            if (choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind != JsonValueKind.Null)
            {
                finish = reason.GetString();
            }
        }
    }

    private void Append(JsonElement element, string property, StringBuilder target)
    {
        if (element.TryGetProperty(property, out var text) && text.ValueKind != JsonValueKind.Null)
        {
            var value = text.GetString();
            var size = Encoding.UTF8.GetByteCount(value ?? string.Empty);
            if (size > maximumPayloadBytes - payloadBytes)
            {
                phase = "payload-size-limit";
                throw new LlmException(LlmFailure.InvalidResponse);
            }
            payloadBytes += size;
            target.Append(value);
        }
    }
}
