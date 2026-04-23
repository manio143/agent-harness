using System.Text.Json;

namespace Agent.Harness.Compaction;

public static class CompactionResponseParser
{
    public static (JsonElement Structured, string ProseSummary) Parse(string? text)
    {
        var fallback = (JsonSerializer.SerializeToElement(new { }), (text ?? string.Empty).Trim());
        if (string.IsNullOrWhiteSpace(text))
            return fallback;

        // 1) Try strict JSON parse.
        if (TryParseJsonObject(text, out var strictRoot))
            return Extract(strictRoot, fallback);

        // 2) Try to extract the first JSON object substring (common when models emit reasoning / extra text).
        var first = text.IndexOf('{');
        var last = text.LastIndexOf('}');
        if (first >= 0 && last > first)
        {
            var slice = text.Substring(first, last - first + 1);
            if (TryParseJsonObject(slice, out var root))
                return Extract(root, fallback);
        }

        return fallback;
    }

    private static bool TryParseJsonObject(string text, out JsonElement root)
    {
        root = default;
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;
            root = doc.RootElement.Clone();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static (JsonElement Structured, string ProseSummary) Extract(JsonElement root, (JsonElement Structured, string ProseSummary) fallback)
    {
        if (!root.TryGetProperty("structured", out var structured))
            return fallback;
        if (!root.TryGetProperty("proseSummary", out var prose))
            return fallback;

        var proseStr = prose.ValueKind == JsonValueKind.String ? (prose.GetString() ?? string.Empty) : prose.ToString();
        return (structured.Clone(), proseStr);
    }
}
