using System.Text.RegularExpressions;
using NJsonSchema;

namespace Agent.Acp.TypeGen;

public static class CodegenPostProcessor
{
    /// <summary>
    /// Post-process NJsonSchema-generated C# to remove known placeholder types deterministically.
    ///
    /// The generator sometimes emits placeholder types (e.g. Content1) for union-heavy references.
    /// We patch those properties based on the schema graph (which properties reference which union defs).
    ///
    /// This is deterministic and schema-driven.
    /// </summary>
    public static string PostProcessGeneratedCode(JsonSchema schema, string code)
    {
        var patchSpecs = new (string TargetDefName, string PlaceholderTypePattern, string ReplacementTypeName)[]
        {
            // ContentBlock refs are often emitted as Content{n}
            ("ContentBlock", "Content\\d+", "ContentBlock"),

            // SessionUpdate refs may be emitted as Update (placeholder)
            ("SessionUpdate", "Update\\d*", "SessionUpdate"),

            // Permission outcomes may be emitted as Outcome (placeholder)
            ("RequestPermissionOutcome", "Outcome\\d*", "RequestPermissionOutcome"),
            ("SelectedPermissionOutcome", "Outcome\\d*", "SelectedPermissionOutcome"),

            // Session config options items
            ("SessionConfigOption", "ConfigOptions", "SessionConfigOption"),

            // Tool call produced content union (NJsonSchema currently leaks a lowercase `content` type)
            ("ToolCallContent", "content", "ToolCallContent"),

            // Tool call kind + status
            ("ToolKind", "Kind\\d*", "ToolKind"),
            ("ToolCallStatus", "Status\\d*", "ToolCallStatus"),

            // Permission option kind
            ("PermissionOptionKind", "Kind\\d*", "PermissionOptionKind"),

            // Plan entry enums
            ("PlanEntryPriority", "Priority", "PlanEntryPriority"),
            ("PlanEntryStatus", "Status\\d*", "PlanEntryStatus"),

            // Stop reason is a string-union in schema; we model it as a global wrapper type.
            ("StopReason", "StopReason\\d*", "StopReason"),
            ("StopReason", "StopReason", "StopReason"),
        };

        foreach (var spec in patchSpecs)
        {
            if (!schema.Definitions.ContainsKey(spec.TargetDefName))
                continue;

            // Direct property refs
            var jsonPropertyNames = FindJsonPropertyNamesReferencing(schema, targetDefName: spec.TargetDefName);
            foreach (var jsonName in jsonPropertyNames)
                code = PatchPropertyType(code, jsonName, spec.PlaceholderTypePattern, spec.ReplacementTypeName);

            // Array item refs
            var arrayItemNames = FindJsonPropertyNamesWhereItemsReference(schema, targetDefName: spec.TargetDefName);
            foreach (var jsonName in arrayItemNames)
                code = PatchCollectionItemType(code, jsonName, spec.PlaceholderTypePattern, spec.ReplacementTypeName);
        }

        // Remove NJsonSchema-emitted partial classes for schema string-const unions.
        // Those defs are modeled as generated wrapper record structs (see UnionGen).
        foreach (var defName in FindStringConstUnionDefinitionNames(schema))
        {
            code = RemovePartialClassDeclaration(code, typeName: defName);
        }

        return code;
    }

    private static string PatchPropertyType(string code, string jsonPropName, string placeholderTypePattern, string replacementTypeName)
    {
        var re = new Regex(
            $"\\[System\\.Text\\.Json\\.Serialization\\.JsonPropertyName\\(\\\"{Regex.Escape(jsonPropName)}\\\"\\)\\]\\s*\\r?\\n\\s*public\\s+(?<type>{placeholderTypePattern})\\s+(?<name>\\w+)\\b",
            RegexOptions.Compiled);

        return re.Replace(code, m =>
        {
            var placeholderType = m.Groups["type"].Value;
            var propName = m.Groups["name"].Value;
            return m.Value.Replace($"public {placeholderType} {propName}", $"public {replacementTypeName} {propName}");
        });
    }

    private static string PatchCollectionItemType(string code, string jsonPropName, string placeholderTypePattern, string replacementTypeName)
    {
        var re = new Regex(
            $"\\[System\\.Text\\.Json\\.Serialization\\.JsonPropertyName\\(\\\"{Regex.Escape(jsonPropName)}\\\"\\)\\]\\s*\\r?\\n\\s*public\\s+System\\.Collections\\.Generic\\.ICollection\\s*<\\s*(?<type>{placeholderTypePattern})\\s*>\\??\\s+(?<name>\\w+)\\b[^\\r\\n]*",
            RegexOptions.Compiled);

        return re.Replace(code, m =>
        {
            var placeholderType = m.Groups["type"].Value;
            return m.Value
                .Replace($"ICollection<{placeholderType}>", $"ICollection<{replacementTypeName}>")
                .Replace($"Collection<{placeholderType}>", $"Collection<{replacementTypeName}>")
                .Replace($"System.Collections.ObjectModel.Collection<{placeholderType}>", $"System.Collections.ObjectModel.Collection<{replacementTypeName}>");
        });
    }

    private static IReadOnlyList<string> FindStringConstUnionDefinitionNames(JsonSchema schema)
    {
        var names = new List<string>();

        var schemaJson = schema.ToJson();
        using var doc = System.Text.Json.JsonDocument.Parse(schemaJson);

        if (!doc.RootElement.TryGetProperty("definitions", out var defs) || defs.ValueKind != System.Text.Json.JsonValueKind.Object)
            return names;

        foreach (var def in defs.EnumerateObject())
        {
            if (!def.Value.TryGetProperty("oneOf", out var oneOf) || oneOf.ValueKind != System.Text.Json.JsonValueKind.Array)
                continue;

            var any = false;
            var ok = true;
            foreach (var item in oneOf.EnumerateArray())
            {
                if (item.TryGetProperty("const", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    any = true;
                    continue;
                }

                ok = false;
                break;
            }

            if (ok && any)
                names.Add(def.Name);
        }

        return names;
    }

    private static string RemovePartialClassDeclaration(string code, string typeName)
    {
        // We remove the first occurrence of a `public partial class <typeName>` block by doing a
        // simple brace-matching pass over lines. This avoids brittle regex spanning.
        var lines = code.Split('\n').ToList();

        var classLineIdx = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Contains($"public partial class {typeName}", StringComparison.Ordinal))
            {
                classLineIdx = i;
                break;
            }
        }

        if (classLineIdx < 0)
            return code;

        // Include preceding doc/attributes up to a blank line.
        var start = classLineIdx;
        while (start > 0 && !string.IsNullOrWhiteSpace(lines[start - 1]))
            start--;

        // Find the opening brace and then match until the block closes.
        var braceDepth = 0;
        var opened = false;
        var end = classLineIdx;
        for (var i = classLineIdx; i < lines.Count; i++)
        {
            foreach (var ch in lines[i])
            {
                if (ch == '{') { braceDepth++; opened = true; }
                else if (ch == '}') braceDepth--;
            }

            if (opened && braceDepth == 0)
            {
                end = i;
                break;
            }
        }

        // Remove inclusive range [start, end]
        lines.RemoveRange(start, end - start + 1);

        return string.Join("\n", lines);
    }

    private static HashSet<string> FindJsonPropertyNamesReferencing(JsonSchema schema, string targetDefName)
    {
        // Prefer scanning the JSON representation of the schema, because NJsonSchema's object model
        // does not consistently preserve reference metadata for allOf/$ref scenarios.
        var byJson = FindJsonPropertyNamesReferencing_ByJson(schema, targetDefName);
        if (byJson.Count > 0)
            return byJson;

        // Fallback to object-model scan.
        if (!schema.Definitions.TryGetValue(targetDefName, out var targetDef))
            return new HashSet<string>(StringComparer.Ordinal);

        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var def in schema.Definitions.Values)
        {
            var props = def?.ActualSchema?.Properties;
            if (props is null)
                continue;

            foreach (var (jsonPropName, prop) in props)
            {
                if (ReferencesDefinition(prop, targetDefName, targetDef))
                    names.Add(jsonPropName);
            }
        }

        return names;
    }

    private static HashSet<string> FindJsonPropertyNamesReferencing_ByJson(JsonSchema schema, string targetDefName)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        var schemaJson = schema.ToJson();
        using var doc = System.Text.Json.JsonDocument.Parse(schemaJson);

        if (!doc.RootElement.TryGetProperty("definitions", out var defs) || defs.ValueKind != System.Text.Json.JsonValueKind.Object)
            return names;

        foreach (var def in defs.EnumerateObject())
        {
            if (!def.Value.TryGetProperty("properties", out var props) || props.ValueKind != System.Text.Json.JsonValueKind.Object)
                continue;

            foreach (var prop in props.EnumerateObject())
            {
                if (JsonElementReferencesDefinition(prop.Value, targetDefName))
                    names.Add(prop.Name);
            }
        }

        return names;
    }

    private static HashSet<string> FindJsonPropertyNamesWhereItemsReference(JsonSchema schema, string targetDefName)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        var schemaJson = schema.ToJson();
        using var doc = System.Text.Json.JsonDocument.Parse(schemaJson);

        if (!doc.RootElement.TryGetProperty("definitions", out var defs) || defs.ValueKind != System.Text.Json.JsonValueKind.Object)
            return names;

        foreach (var def in defs.EnumerateObject())
        {
            if (!def.Value.TryGetProperty("properties", out var props) || props.ValueKind != System.Text.Json.JsonValueKind.Object)
                continue;

            foreach (var prop in props.EnumerateObject())
            {
                if (prop.Value.ValueKind != System.Text.Json.JsonValueKind.Object)
                    continue;

                if (!prop.Value.TryGetProperty("items", out var items))
                    continue;

                if (JsonElementReferencesDefinition(items, targetDefName))
                    names.Add(prop.Name);
            }
        }

        return names;
    }

    private static bool JsonElementReferencesDefinition(System.Text.Json.JsonElement el, string targetDefName)
    {
        // Detect any nested { "$ref": "#/definitions/<target>" }
        if (el.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            if (el.TryGetProperty("$ref", out var r) && r.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var s = r.GetString() ?? string.Empty;
                if (s.EndsWith($"/definitions/{targetDefName}", StringComparison.Ordinal) || s.EndsWith($"#/definitions/{targetDefName}", StringComparison.Ordinal))
                    return true;
            }

            foreach (var p in el.EnumerateObject())
            {
                if (JsonElementReferencesDefinition(p.Value, targetDefName))
                    return true;
            }
        }
        else if (el.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
            {
                if (JsonElementReferencesDefinition(item, targetDefName))
                    return true;
            }
        }

        return false;
    }

    private static bool ReferencesDefinition(JsonSchemaProperty prop, string targetDefName, JsonSchema targetDef)
    {
        // Prefer object identity when possible (most robust).
        if (ReferenceEquals(prop.Reference, targetDef)) return true;
        if (ReferenceEquals(prop.ActualSchema?.Reference, targetDef)) return true;

        // Fallback to name extraction.
        if (RefName(prop.Reference) == targetDefName) return true;
        if (RefName(prop.ActualSchema?.Reference) == targetDefName) return true;

        var allOf = prop.ActualSchema?.AllOf;
        if (allOf is not null)
        {
            foreach (var s in allOf)
            {
                if (ReferenceEquals(s.Reference, targetDef)) return true;
                if (ReferenceEquals(s.ActualSchema?.Reference, targetDef)) return true;

                if (RefName(s.Reference) == targetDefName) return true;
                if (RefName(s.ActualSchema?.Reference) == targetDefName) return true;
            }
        }

        return false;
    }

    private static string? RefName(JsonSchema? schema)
    {
        if (schema is null) return null;

        if (!string.IsNullOrWhiteSpace(schema.Title))
            return schema.Title;

        // When loading from JSON, Title may not be set on referenced schemas.
        // DocumentPath is typically something like "#/definitions/Foo".
        if (!string.IsNullOrWhiteSpace(schema.DocumentPath))
        {
            var p = schema.DocumentPath;
            var idx = p.LastIndexOf('/');
            if (idx >= 0 && idx + 1 < p.Length)
                return p[(idx + 1)..];
        }

        return null;
    }
}
