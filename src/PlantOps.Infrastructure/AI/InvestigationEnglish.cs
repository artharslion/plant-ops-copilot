using System.Text.Json;
using System.Text.RegularExpressions;

namespace PlantOps.Infrastructure.AI;

// Presentation for generated drafts only; the canonical investigation ledger is untouched.
internal static class InvestigationEnglish
{
    private static readonly JsonElement Catalog = Load();
    private static readonly (string[] Kinds, Regex Pattern, string English, int[] Slots)[] Rules = Catalog.GetProperty("rules").EnumerateArray().Select(rule =>
    {
        var source = Regex.Escape(rule.GetProperty("source").GetString()!);
        var seen = new HashSet<string>();
        var pattern = Regex.Replace(source, @"\\\{(\d+)}", match => seen.Add(match.Groups[1].Value)
            ? $"(?<p{match.Groups[1].Value}>.*?)" : $@"\k<p{match.Groups[1].Value}>");
        return (rule.GetProperty("kinds").EnumerateArray().Select(x => x.GetString()!).ToArray(),
            new Regex("^" + pattern + "$", RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)),
            rule.GetProperty("english").GetString()!, rule.GetProperty("localizedSlots").EnumerateArray().Select(x => x.GetInt32()).ToArray());
    }).ToArray();

    private static JsonElement Load()
    {
        using var stream = typeof(InvestigationEnglish).Assembly.GetManifestResourceStream("PlantOps.Infrastructure.AI.EvidenceEnglish.json")!;
        using var json = JsonDocument.Parse(stream);
        return json.RootElement.Clone();
    }

    public static string Label(string value) => Catalog.GetProperty("labels").TryGetProperty(value, out var english) ? english.GetString()! : value;

    public static string Fact(string kind, string value) => string.Join('\n', value.Split('\n').Select(line =>
    {
        const string note = "锚点所在分钟尚未结束，以上聚合不含锚点所在分钟。";
        var hasNote = kind is "RelatedTrend" or "RelatedComparison" && line.EndsWith(note, StringComparison.Ordinal);
        var text = hasNote ? line[..^note.Length] : line;
        foreach (var rule in Rules.Where(x => x.Kinds.Contains(kind)))
        {
            var match = rule.Pattern.Match(text);
            if (!match.Success)
            {
                continue;
            }
            return Regex.Replace(rule.English, @"\{(\d+)}", token =>
            {
                var slot = int.Parse(token.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                var captured = match.Groups["p" + slot].Value;
                return rule.Slots.Contains(slot) && Catalog.GetProperty("terms").TryGetProperty(captured, out var term) ? term.GetString()! : captured;
            }) + (hasNote ? " The anchor minute has not ended and is excluded from these aggregates." : "");
        }
        return line;
    }));

    public static string Title(string kind, string title)
    {
        if (kind == "PointStatus" && title.StartsWith("测点证据状态与按需历史窗口 · ", StringComparison.Ordinal))
        {
            return "Point evidence status and requested historical windows · " + title["测点证据状态与按需历史窗口 · ".Length..];
        }
        if (kind == "PointStatus" && title.StartsWith("测点证据状态 · ", StringComparison.Ordinal))
        {
            return "Point evidence status · " + title["测点证据状态 · ".Length..];
        }
        if (kind == "RelatedTrend" && title.StartsWith("关联点位同期趋势 · ", StringComparison.Ordinal))
        {
            return "Related point concurrent trends · " + title["关联点位同期趋势 · ".Length..];
        }
        if (kind == "Document")
        {
            return Regex.Replace(title, @" · 版本 (\d+)(.*?) · 行 (\d+)–(\d+)$", " · Version $1$2 · Lines $3–$4", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        }
        if (Catalog.GetProperty("titles").TryGetProperty(kind, out var titles) && titles.TryGetProperty(title, out var translated))
        {
            return translated.GetString()!;
        }
        return Catalog.GetProperty("terms").TryGetProperty(title, out var term) ? term.GetString()! : title;
    }
}
