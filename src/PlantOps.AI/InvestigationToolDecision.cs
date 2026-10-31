using System.Text.Json;
using System.Text.Json.Serialization;
using PlantOps.Application.AI;

namespace PlantOps.AI;

internal sealed record InvestigationToolDecision(string Decision)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 4
    };

    public static LlmOutputSchema Schema { get; } = CreateSchema();

    private static LlmOutputSchema CreateSchema()
    {
        using var document = JsonDocument.Parse("""
            {"type":"object","additionalProperties":false,"required":["decision"],
             "properties":{"decision":{"type":"string","enum":["Ready"]}}}
            """);
        return new("investigation_evidence_decision", document.RootElement.Clone());
    }

    public static bool IsReady(string? content)
    {
        try
        {
            return content != null && JsonSerializer.Deserialize<InvestigationToolDecision>(content, Json)?.Decision == "Ready";
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
