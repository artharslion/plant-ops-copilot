using System.Text.Json;
using System.Text.RegularExpressions;
using PlantOps.Application.AI;

namespace PlantOps.AI;

internal static partial class ChatCompletionsProtocol
{
    [GeneratedRegex("^[A-Za-z0-9_-]{1,100}$", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();

    public static byte[] Serialize(LlmRequest request, string model, string? reasoningEffort = null)
    {
        var tools = request.Tools ?? [];
        if (request.Messages.Count is < 1 or > 32 || request.MaxOutputTokens is < 1 or > 2000
            || tools.Count > 5 || tools.Any(x => !Identifier().IsMatch(x.Name) || x.Parameters.ValueKind != JsonValueKind.Object)
            || tools.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != tools.Count
            || (request.RequiredTool is not null && (request.DisableToolCalls || !tools.Any(x => x.Name == request.RequiredTool)))
            || (request.OutputSchema is not null && (!Identifier().IsMatch(request.OutputSchema.Name)
                || request.OutputSchema.Schema.ValueKind != JsonValueKind.Object)))
        {
            throw new LlmException(LlmFailure.InvalidRequest);
        }
        var messages = new List<Dictionary<string, object?>>();
        var pending = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in request.Messages)
        {
            if (!Enum.IsDefined(message.Role) || (message.Role != LlmRole.Tool && pending.Count != 0)
                || (message.ToolCalls is { Count: > 0 } && message.Role != LlmRole.Assistant)
                || (message.ToolCallId is not null && message.Role != LlmRole.Tool))
            {
                throw new LlmException(LlmFailure.InvalidRequest);
            }
            var item = new Dictionary<string, object?> { ["role"] = message.Role.ToString().ToLowerInvariant(), ["content"] = message.Content };
            if (message.Role == LlmRole.Tool)
            {
                if (message.ToolCallId is null || !pending.Remove(message.ToolCallId) || message.Content is null)
                {
                    throw new LlmException(LlmFailure.InvalidRequest);
                }
                item["tool_call_id"] = message.ToolCallId;
            }
            if (message.ToolCalls is { Count: > 0 })
            {
                foreach (var call in message.ToolCalls)
                {
                    ValidateCall(call, tools);
                    if (!seen.Add(call.Id))
                    {
                        throw new LlmException(LlmFailure.InvalidRequest);
                    }
                    pending.Add(call.Id);
                }
                item["tool_calls"] = message.ToolCalls.Select(call => new
                {
                    id = call.Id,
                    type = "function",
                    function = new
                    {
                        name = call.Name,
                        arguments = call.Arguments
                    }
                }).ToArray();
            }
            else if (message.Content is null)
            {
                throw new LlmException(LlmFailure.InvalidRequest);
            }
            messages.Add(item);
        }
        if (pending.Count > 0)
        {
            throw new LlmException(LlmFailure.InvalidRequest);
        }
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = messages,
            ["stream"] = false,
            ["max_completion_tokens"] = request.MaxOutputTokens
        };
        if (!string.IsNullOrEmpty(reasoningEffort))
        {
            payload["reasoning_effort"] = reasoningEffort;
        }
        if (tools.Count > 0)
        {
            payload["tools"] = tools.Select(tool => new
            {
                type = "function",
                function = new
                {
                    name = tool.Name,
                    description = tool.Description,
                    parameters = tool.Parameters,
                    strict = true
                }
            }).ToArray();
            payload["parallel_tool_calls"] = false;
            payload["tool_choice"] = request.DisableToolCalls ? (object)"none" : request.RequiredTool is null ? (object)"auto"
                : new
                {
                    type = "function",
                    function = new
                    {
                        name = request.RequiredTool
                    }
                };
        }
        if (request.OutputSchema is not null)
        {
            payload["response_format"] = new
            {
                type = "json_schema",
                json_schema = new
                {
                    name = request.OutputSchema.Name,
                    strict = true,
                    schema = request.OutputSchema.Schema
                }
            };
        }
        return JsonSerializer.SerializeToUtf8Bytes(payload);
    }

    public static LlmCompletion Parse(byte[] bytes, LlmRequest request)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            var choices = root.GetProperty("choices");
            if (choices.GetArrayLength() != 1)
            {
                throw new LlmException(LlmFailure.InvalidResponse);
            }
            var choice = choices[0];
            var finish = choice.GetProperty("finish_reason").GetString();
            if (finish == "length")
            {
                throw new LlmException(LlmFailure.Truncated);
            }
            if (finish == "content_filter")
            {
                throw new LlmException(LlmFailure.Refused);
            }
            if (finish is not ("stop" or "tool_calls"))
            {
                throw new LlmException(LlmFailure.InvalidResponse);
            }
            var message = choice.GetProperty("message");
            if (message.GetProperty("role").GetString() != "assistant")
            {
                throw new LlmException(LlmFailure.InvalidResponse);
            }
            if (message.TryGetProperty("refusal", out var refusal) && refusal.ValueKind != JsonValueKind.Null
                && !string.IsNullOrEmpty(refusal.GetString()))
            {
                throw new LlmException(LlmFailure.Refused);
            }
            string? content = null;
            if (message.TryGetProperty("content", out var text) && text.ValueKind != JsonValueKind.Null)
            {
                content = text.GetString();
            }
            var calls = new List<LlmToolCall>();
            if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind != JsonValueKind.Null)
            {
                if (toolCalls.GetArrayLength() > 8)
                {
                    throw new LlmException(LlmFailure.BudgetExceeded);
                }
                foreach (var item in toolCalls.EnumerateArray())
                {
                    if (item.GetProperty("type").GetString() != "function")
                    {
                        throw new LlmException(LlmFailure.InvalidResponse);
                    }
                    var function = item.GetProperty("function");
                    var call = new LlmToolCall(item.GetProperty("id").GetString()!, function.GetProperty("name").GetString()!,
                        function.GetProperty("arguments").GetString()!);
                    try
                    {
                        ValidateCall(call, request.Tools ?? []);
                    }
                    catch (LlmException) { throw new LlmException(LlmFailure.InvalidResponse); }
                    if (calls.Any(x => x.Id == call.Id))
                    {
                        throw new LlmException(LlmFailure.InvalidResponse);
                    }
                    calls.Add(call);
                }
            }
            if ((request.DisableToolCalls && calls.Count > 0) || (finish == "tool_calls") != (calls.Count > 0) || (calls.Count == 0 && string.IsNullOrWhiteSpace(content))
                || (request.RequiredTool is not null && (calls.Count != 1 || calls[0].Name != request.RequiredTool)))
            {
                throw new LlmException(LlmFailure.InvalidResponse);
            }
            LlmUsage? usage = null;
            if (root.TryGetProperty("usage", out var usageElement) && usageElement.ValueKind != JsonValueKind.Null)
            {
                var input = usageElement.GetProperty("prompt_tokens").GetInt32();
                var output = usageElement.GetProperty("completion_tokens").GetInt32();
                if (input < 0 || output < 0)
                {
                    throw new LlmException(LlmFailure.InvalidResponse);
                }
                usage = new(input, output);
            }
            var model = root.GetProperty("model").GetString();
            if (string.IsNullOrWhiteSpace(model) || model.Length > 200 || model.Any(char.IsControl))
            {
                throw new LlmException(LlmFailure.InvalidResponse);
            }
            return new(content, calls, model, usage);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or ArgumentException)
        {
            throw new LlmException(LlmFailure.InvalidResponse);
        }
    }

    private static void ValidateCall(LlmToolCall call, IReadOnlyList<LlmTool> tools)
    {
        if (string.IsNullOrEmpty(call.Id) || !Identifier().IsMatch(call.Id) || string.IsNullOrEmpty(call.Name)
            || !tools.Any(x => x.Name == call.Name) || string.IsNullOrEmpty(call.Arguments) || call.Arguments.Length > 8192)
        {
            throw new LlmException(LlmFailure.InvalidRequest);
        }
        try
        {
            using var args = JsonDocument.Parse(call.Arguments, new JsonDocumentOptions { MaxDepth = 16 });
            if (args.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new LlmException(LlmFailure.InvalidRequest);
            }
        }
        catch (JsonException) { throw new LlmException(LlmFailure.InvalidRequest); }
    }
}
