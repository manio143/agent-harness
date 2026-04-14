using System.Collections.Immutable;
using System.Linq;
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

        if (schema.TryGetProperty("type", out var typeEl))
        {
            // type can be string or [..] in JSON Schema.
            if (!SchemaAllowsType(typeEl, "object"))
                return ImmutableArray.Create("schema_not_object");
        }

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

        var hasProps = schema.TryGetProperty("properties", out var propsEl) && propsEl.ValueKind == JsonValueKind.Object;

        if (hasProps && argsEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in propsEl.EnumerateObject())
            {
                if (!argsEl.TryGetProperty(prop.Name, out var argVal)) continue;
                if (prop.Value.ValueKind != JsonValueKind.Object) continue;

                if (prop.Value.TryGetProperty("type", out var propTypeEl))
                {
                    if (!MatchesType(argVal, propTypeEl))
                    {
                        var expected = RenderTypeHint(propTypeEl);
                        errors.Add($"type_mismatch:{prop.Name}:{expected}");
                    }
                }
            }
        }

        // Enforce additionalProperties=false (when present) by rejecting unknown args.
        if (argsEl.ValueKind == JsonValueKind.Object &&
            schema.TryGetProperty("additionalProperties", out var apEl) &&
            apEl.ValueKind == JsonValueKind.False)
        {
            var allowed = hasProps
                ? ImmutableHashSet.CreateRange(propsEl.EnumerateObject().Select(p => p.Name))
                : ImmutableHashSet<string>.Empty;

            foreach (var argProp in argsEl.EnumerateObject())
            {
                if (!allowed.Contains(argProp.Name))
                    errors.Add($"unexpected_arg:{argProp.Name}");
            }
        }

        return errors.ToImmutable();
    }

    private static bool SchemaAllowsType(JsonElement typeEl, string wanted)
        => typeEl.ValueKind switch
        {
            JsonValueKind.String => string.Equals(typeEl.GetString(), wanted, StringComparison.OrdinalIgnoreCase),
            JsonValueKind.Array => typeEl.EnumerateArray().Any(t => t.ValueKind == JsonValueKind.String && string.Equals(t.GetString(), wanted, StringComparison.OrdinalIgnoreCase)),
            _ => true, // unknown schema shape => don't block
        };

    private static string RenderTypeHint(JsonElement typeEl)
        => typeEl.ValueKind switch
        {
            JsonValueKind.String => typeEl.GetString() ?? "unknown",
            JsonValueKind.Array => string.Join("|", typeEl.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString())),
            _ => "unknown",
        };

    private static bool MatchesType(JsonElement value, JsonElement typeEl)
    {
        if (typeEl.ValueKind == JsonValueKind.String)
            return MatchesType(value, typeEl.GetString());

        if (typeEl.ValueKind == JsonValueKind.Array)
            return typeEl.EnumerateArray().Any(t => t.ValueKind == JsonValueKind.String && MatchesType(value, t.GetString()));

        // unknown schema shape => don't block
        return true;
    }

    private static bool MatchesType(JsonElement value, string? expected)
    {
        var kind = value.ValueKind;
        return expected switch
        {
            null => true,
            "null" => kind == JsonValueKind.Null,
            "string" => kind == JsonValueKind.String,
            "number" => kind == JsonValueKind.Number,
            "integer" => kind == JsonValueKind.Number && value.TryGetInt64(out _),
            "boolean" => kind is JsonValueKind.True or JsonValueKind.False,
            "object" => kind == JsonValueKind.Object,
            "array" => kind == JsonValueKind.Array,
            _ => true,
        };
    }
}
