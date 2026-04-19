using System.Text.Json;

namespace Agent.Harness.Tools;

public static class ToolArgs
{
    public static Dictionary<string, JsonElement> Normalize(object args)
    {
        if (args is JsonElement je && je.ValueKind == JsonValueKind.Object)
            return je.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);

        var parsed = JsonSerializer.SerializeToElement(args, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (parsed.ValueKind != JsonValueKind.Object)
            return new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        return parsed.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);
    }
}
