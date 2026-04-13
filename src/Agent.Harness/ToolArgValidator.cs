using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;

namespace Agent.Harness;

public static class ToolArgValidator
{
    public static ImmutableArray<string> Validate(JsonElement schema, object args)
    {
        // MVP schema support:
        // - type: object
        // - properties: { name: { type: <primitive> } }
        // - required: [..]
        // Anything else is ignored (non-blocking).

        using var argsDoc = JsonDocument.Parse(JsonSerializer.Serialize(args));
        var argsEl = argsDoc.RootElement;

        if (schema.ValueKind != JsonValueKind.Object)
            return ImmutableArray<string>.Empty;

        if (schema.TryGetProperty("type", out var typeEl) && typeEl.GetString() is string t && t != "object")
            return ImmutableArray.Create("schema_not_object");

        var errors = ImmutableArray.CreateBuilder<string>();

        if (schema.TryGetProperty("required", out var requiredEl) && requiredEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var req in requiredEl.EnumerateArray())
            {
                if (req.ValueKind != JsonValueKind.String) continue;
                var name = req.GetString()!;

                if (argsEl.ValueKind != JsonValueKind.Object || !argsEl.TryGetProperty(name, out _))
                {
                    errors.Add($"missing_required:{name}");
                }
            }
        }

        if (schema.TryGetProperty("properties", out var propsEl) && propsEl.ValueKind == JsonValueKind.Object && argsEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in propsEl.EnumerateObject())
            {
                if (!argsEl.TryGetProperty(prop.Name, out var argVal)) continue;
                if (prop.Value.ValueKind != JsonValueKind.Object) continue;

                if (prop.Value.TryGetProperty("type", out var propTypeEl) && propTypeEl.ValueKind == JsonValueKind.String)
                {
                    var expected = propTypeEl.GetString();
                    if (!MatchesType(argVal.ValueKind, expected))
                    {
                        errors.Add($"type_mismatch:{prop.Name}:{expected}");
                    }
                }
            }
        }

        return errors.ToImmutable();
    }

    private static bool MatchesType(JsonValueKind kind, string? expected)
        => expected switch
        {
            null => true,
            "string" => kind == JsonValueKind.String,
            "number" => kind == JsonValueKind.Number,
            "integer" => kind == JsonValueKind.Number,
            "boolean" => kind is JsonValueKind.True or JsonValueKind.False,
            "object" => kind == JsonValueKind.Object,
            "array" => kind == JsonValueKind.Array,
            _ => true,
        };
}
