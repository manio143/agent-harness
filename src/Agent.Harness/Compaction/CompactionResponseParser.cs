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

        // 2) Try to extract a JSON object that contains a top-level "structured" property.
        // Some models include example brace blocks inside reasoning; picking the first '{' is too naive.
        if (TryExtractStructuredObject(text, out var extracted))
            return Extract(extracted, fallback);

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

    private static bool TryExtractStructuredObject(string text, out JsonElement root)
    {
        root = default;

        var idx = 0;
        while (idx < text.Length)
        {
            var structuredPos = text.IndexOf("\"structured\"", idx, StringComparison.Ordinal);
            if (structuredPos < 0)
                return false;

            // Find the nearest '{' before the "structured" token.
            var start = text.LastIndexOf('{', structuredPos);
            if (start < 0)
            {
                idx = structuredPos + 1;
                continue;
            }

            if (TryExtractJsonObjectAt(text, start, out var extracted) && extracted.ValueKind == JsonValueKind.Object)
            {
                // Ensure it looks like our expected schema.
                if (extracted.TryGetProperty("structured", out _) && extracted.TryGetProperty("proseSummary", out _))
                {
                    root = extracted;
                    return true;
                }
            }

            idx = structuredPos + 1;
        }

        return false;
    }

    private static bool TryExtractJsonObjectAt(string text, int start, out JsonElement root)
    {
        root = default;

        var depth = 0;
        var inStr = false;
        var escape = false;

        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];

            if (inStr)
            {
                if (escape)
                {
                    escape = false;
                    continue;
                }

                if (c == '\\')
                {
                    escape = true;
                    continue;
                }

                if (c == '"')
                {
                    inStr = false;
                    continue;
                }

                continue;
            }

            if (c == '"')
            {
                inStr = true;
                continue;
            }

            if (c == '{')
            {
                depth++;
                continue;
            }

            if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    var slice = text.Substring(start, i - start + 1);
                    return TryParseJsonObject(slice, out root);
                }
            }
        }

        return false;
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
