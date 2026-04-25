using System.Text.Json;

namespace Agent.Harness.Llm;

internal static class ToolResultSanitizer
{
    public sealed record SanitizedToolResult(object? Value, bool WasTruncated);

    public static bool IsEnabled =>
        Environment.GetEnvironmentVariable("AGENT_TOOL_RESULT_SANITIZE") == "1" ||
        Environment.GetEnvironmentVariable("AGENT_TOOL_RESULT_MAX_STRING_CHARS") is not null ||
        Environment.GetEnvironmentVariable("AGENT_TOOL_RESULT_MAX_ARRAY_ITEMS") is not null ||
        Environment.GetEnvironmentVariable("AGENT_TOOL_RESULT_MAX_OBJECT_PROPS") is not null ||
        Environment.GetEnvironmentVariable("AGENT_TOOL_RESULT_MAX_DEPTH") is not null;

    // Keep tool results small to avoid blowing provider TPM limits.
    // When enabled, this caps tool outputs at the observed/committed event level.
    public const int DefaultMaxStringChars = 800;
    public const int DefaultMaxArrayItems = 50;
    public const int DefaultMaxObjectProperties = 50;
    public const int DefaultMaxDepth = 12;

    public static int EffectiveMaxStringChars =>
        int.TryParse(Environment.GetEnvironmentVariable("AGENT_TOOL_RESULT_MAX_STRING_CHARS"), out var v) && v > 0 ? v : DefaultMaxStringChars;

    public static int EffectiveMaxArrayItems =>
        int.TryParse(Environment.GetEnvironmentVariable("AGENT_TOOL_RESULT_MAX_ARRAY_ITEMS"), out var v) && v > 0 ? v : DefaultMaxArrayItems;

    public static int EffectiveMaxObjectProperties =>
        int.TryParse(Environment.GetEnvironmentVariable("AGENT_TOOL_RESULT_MAX_OBJECT_PROPS"), out var v) && v > 0 ? v : DefaultMaxObjectProperties;

    public static int EffectiveMaxDepth =>
        int.TryParse(Environment.GetEnvironmentVariable("AGENT_TOOL_RESULT_MAX_DEPTH"), out var v) && v > 0 ? v : DefaultMaxDepth;

    public static SanitizedToolResult Sanitize(object? value,
        int? maxStringChars = null,
        int? maxArrayItems = null,
        int? maxObjectProperties = null,
        int? maxDepth = null)
    {
        try
        {
            if (value is null)
                return new SanitizedToolResult(null, WasTruncated: false);

            // Convert to JsonElement so we can walk it uniformly.
            var el = value is JsonElement je
                ? je
                : JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

            var ms = maxStringChars ?? EffectiveMaxStringChars;
            var ma = maxArrayItems ?? EffectiveMaxArrayItems;
            var mo = maxObjectProperties ?? EffectiveMaxObjectProperties;
            var md = maxDepth ?? EffectiveMaxDepth;

            var (sanitized, truncated) = SanitizeElement(el,
                maxStringChars: ms,
                maxArrayItems: ma,
                maxObjectProperties: mo,
                maxDepth: md,
                depth: 0);

            return new SanitizedToolResult(sanitized, truncated);
        }
        catch
        {
            // Best-effort: never allow sanitization failures to break a run.
            return new SanitizedToolResult(value, WasTruncated: false);
        }
    }

    private static (object? Value, bool WasTruncated) SanitizeElement(JsonElement el,
        int maxStringChars,
        int maxArrayItems,
        int maxObjectProperties,
        int maxDepth,
        int depth)
    {
        if (depth >= maxDepth)
            return ("<omitted depth_limit=true>", true);

        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
                var count = 0;
                var truncated = false;

                foreach (var p in el.EnumerateObject())
                {
                    if (count >= maxObjectProperties)
                    {
                        truncated = true;
                        break;
                    }

                    var (v, tv) = SanitizeElement(p.Value, maxStringChars, maxArrayItems, maxObjectProperties, maxDepth, depth + 1);
                    dict[p.Name] = v;
                    truncated |= tv;
                    count++;
                }

                var total = el.EnumerateObject().Count();
                if (total > count)
                {
                    dict["_omittedProperties"] = total - count;
                    truncated = true;
                }

                return (dict, truncated);
            }

            case JsonValueKind.Array:
            {
                var list = new List<object?>();
                var i = 0;
                var truncated = false;

                foreach (var item in el.EnumerateArray())
                {
                    if (i >= maxArrayItems)
                    {
                        truncated = true;
                        break;
                    }

                    var (v, tv) = SanitizeElement(item, maxStringChars, maxArrayItems, maxObjectProperties, maxDepth, depth + 1);
                    list.Add(v);
                    truncated |= tv;
                    i++;
                }

                var total = el.GetArrayLength();
                if (total > i)
                {
                    list.Add($"<omitted items={total - i}>");
                    truncated = true;
                }

                return (list, truncated);
            }

            case JsonValueKind.String:
            {
                var s = el.GetString() ?? string.Empty;
                var (t, truncated) = TruncateString(s, maxStringChars);
                return (t, truncated);
            }

            case JsonValueKind.Number:
                if (el.TryGetInt64(out var l)) return (l, false);
                if (el.TryGetDouble(out var d)) return (d, false);
                return (el.GetRawText(), false);

            case JsonValueKind.True:
                return (true, false);
            case JsonValueKind.False:
                return (false, false);
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return (null, false);

            default:
                return (el.GetRawText(), false);
        }
    }

    private static (string Value, bool WasTruncated) TruncateString(string s, int maxChars)
    {
        if (maxChars <= 0) return (string.Empty, s.Length > 0);
        if (s.Length <= maxChars) return (s, false);

        var head = s[..maxChars];
        return (head + $"\n\n[TRUNCATED original_length={s.Length}]", true);
    }
}
