using System.Text.Json;
using System.Text.RegularExpressions;

namespace PlantOps.Application.AI;

public static class InvestigationToolPolicy
{
    public static void Validate(string name, JsonElement arguments)
    {
        if (name is not ("get_asset_details" or "get_recent_measurements" or "get_recent_alarms" or "get_maintenance_history" or "search_documents") || arguments.ValueKind != JsonValueKind.Object)
        {
            throw new LlmException(LlmFailure.InvalidRequest);
        }
        var properties = arguments.EnumerateObject().ToArray();
        if (name != "search_documents")
        {
            if (properties.Length != 0)
            {
                throw new LlmException(LlmFailure.InvalidRequest);
            }
            return;
        }
        if (properties.Length != 2 || !arguments.TryGetProperty("query", out var query) || query.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(query.GetString()) || query.GetString()!.Length > 200
            || !arguments.TryGetProperty("limit", out var limit) || !limit.TryGetInt32(out var size) || size is < 1 or > 6)
        {
            throw new LlmException(LlmFailure.InvalidRequest);
        }
        var terms = Regex.Matches(query.GetString()!, @"[\p{L}\p{N}][\p{L}\p{N}_\-]*", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))
            .Select(x => x.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        if (terms is < 1 or > 12)
        {
            throw new LlmException(LlmFailure.InvalidRequest);
        }
    }

}
