using System.Text.Json;

namespace Agent.Harness.Compaction;

public static class CompactionResponseParser
{
    /// <summary>
    /// Extracts the compaction text from a model response.
    ///
    /// Preferred format is a single <compaction>...</compaction> block.
    ///
    /// Back-compat: if the response is JSON and contains a proseSummary/text field,
    /// those are used as the compaction text.
    /// </summary>
    public static string Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var trimmed = text.Trim();

        // 1) Preferred: extract <compaction> block (case-insensitive).
        var block = TryExtractTagBlock(trimmed, "<compaction>", "</compaction>");
        if (block is not null)
            return block;

        // 2) Back-compat: try JSON object and prefer proseSummary/text.
        if (TryParseJsonObject(trimmed, out var root))
        {
            if (root.TryGetProperty("proseSummary", out var prose))
                return ReadStringish(prose);

            if (root.TryGetProperty("text", out var t))
                return ReadStringish(t);
        }

        // 3) Fallback: return whole response.
        return trimmed;
    }

    private static string? TryExtractTagBlock(string text, string startTag, string endTag)
    {
        var start = text.IndexOf(startTag, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return null;

        var end = text.IndexOf(endTag, start + startTag.Length, StringComparison.OrdinalIgnoreCase);
        if (end < 0)
            return null;

        return text.Substring(start, (end - start) + endTag.Length).Trim();
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

    private static string ReadStringish(JsonElement el)
    {
        try
        {
            return el.ValueKind == JsonValueKind.String ? (el.GetString() ?? string.Empty) : el.ToString();
        }
        catch
        {
            return el.ToString();
        }
    }
}
