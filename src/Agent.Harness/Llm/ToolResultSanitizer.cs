using System.Text.Json;

namespace Agent.Harness.Llm;

internal static class ToolResultSanitizer
{
    public sealed record SanitizedToolResult(object? Value, bool WasTruncated);

    // Keep tool results small to avoid blowing provider TPM limits.
    // When enabled (by the host passing ToolResultCappingOptions.Enabled=true), this caps tool outputs
    // at the observed/committed event level.
    public const int DefaultMaxStringChars = 800;
    public const int DefaultMaxArrayItems = 50;
    public const int DefaultMaxObjectProperties = 50;
    public const int DefaultMaxDepth = 12;

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

            var ms = maxStringChars is > 0 ? maxStringChars.Value : DefaultMaxStringChars;
            var ma = maxArrayItems is > 0 ? maxArrayItems.Value : DefaultMaxArrayItems;
            var mo = maxObjectProperties is > 0 ? maxObjectProperties.Value : DefaultMaxObjectProperties;
            var md = maxDepth is > 0 ? maxDepth.Value : DefaultMaxDepth;

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
