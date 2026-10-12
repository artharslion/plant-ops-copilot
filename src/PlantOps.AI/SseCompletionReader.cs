using System.Text;
using System.Text.Json;
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

    public static async Task<LlmCompletion> ReadAsync(HttpContent http, int maximumBytes, LlmRequest request, CancellationToken ct)
    {
        if (http.Headers.ContentType?.MediaType != "text/event-stream" || http.Headers.ContentLength > maximumBytes)
        {
            throw new LlmException(LlmFailure.InvalidResponse);
        }
        var reader = new SseCompletionReader();
        await using var stream = await http.ReadAsStreamAsync(ct);
        using var line = new MemoryStream();
        var data = new StringBuilder();
        var buffer = new byte[4096];
        var total = 0;
        try
        {
            while (!reader.done)
            {
                var read = await stream.ReadAsync(buffer, ct);
                if (read == 0)
                {
                    throw new LlmException(LlmFailure.InvalidResponse);
                }
                if (read > maximumBytes - total)
                {
                    throw new LlmException(LlmFailure.InvalidResponse);
                }
                total += read;
                for (var i = 0; i < read && !reader.done; i++)
                {
                    if (buffer[i] != (byte)'\n')
                    {
                        line.WriteByte(buffer[i]);
                        continue;
                    }
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
            return ChatCompletionsProtocol.Parse(bytes, request);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException
            or FormatException or OverflowException or ArgumentException)
        {
            throw new LlmException(LlmFailure.InvalidResponse);
        }
    }

    private void Accept(string data)
    {
        if (data == "[DONE]")
        {
            done = true;
            return;
        }
        using var document = JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        var nextModel = root.GetProperty("model").GetString();
        if (string.IsNullOrWhiteSpace(nextModel) || model != null && model != nextModel)
        {
            throw new LlmException(LlmFailure.InvalidResponse);
        }
        model = nextModel;
        if (root.TryGetProperty("usage", out var reported) && reported.ValueKind != JsonValueKind.Null)
        {
            if (usage.HasValue)
            {
                throw new LlmException(LlmFailure.InvalidResponse);
            }
            usage = reported.Clone();
        }
        var choices = root.GetProperty("choices");
        if (choices.GetArrayLength() > 1)
        {
            throw new LlmException(LlmFailure.InvalidResponse);
        }
        foreach (var choice in choices.EnumerateArray())
        {
            if (choice.GetProperty("index").GetInt32() != 0 || finish != null)
            {
                throw new LlmException(LlmFailure.InvalidResponse);
            }
            var delta = choice.GetProperty("delta");
            if (delta.TryGetProperty("role", out var role) && role.GetString() != "assistant")
            {
                throw new LlmException(LlmFailure.InvalidResponse);
            }
            Append(delta, "content", content);
            Append(delta, "refusal", refusal);
            if (delta.TryGetProperty("tool_calls", out var tools))
            {
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

    private static void Append(JsonElement element, string property, StringBuilder target)
    {
        if (element.TryGetProperty(property, out var text) && text.ValueKind != JsonValueKind.Null)
        {
            target.Append(text.GetString());
        }
    }
}
