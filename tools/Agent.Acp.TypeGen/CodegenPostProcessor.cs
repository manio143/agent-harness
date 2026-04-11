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
        // For now, we have one proven placeholder pattern:
        // - properties that reference ContentBlock may be generated as Content{n} placeholder types.
        // We compute which JSON property names reference ContentBlock from the schema and patch
        // any matching properties in generated C#.

        if (!schema.Definitions.TryGetValue("ContentBlock", out var contentBlockDef))
            return code;

        var jsonPropertyNames = FindJsonPropertyNamesReferencing(schema, targetDefName: "ContentBlock");
        if (jsonPropertyNames.Count == 0)
            return code;

        foreach (var jsonName in jsonPropertyNames)
        {
            // Patch any property with [JsonPropertyName("<jsonName>")] where the type is a Content{n} placeholder.
            // Example:
            //   [JsonPropertyName("payload")]
            //   public Content1 Payload { get; set; }
            // becomes:
            //   public ContentBlock Payload { get; set; }
            var re = new Regex(
                $"\\[System\\.Text\\.Json\\.Serialization\\.JsonPropertyName\\(\\\"{Regex.Escape(jsonName)}\\\"\\)\\]\\s*\\r?\\n\\s*public\\s+(?<type>Content\\d+)\\s+(?<name>\\w+)\\b",
                RegexOptions.Compiled);

            code = re.Replace(code, m =>
            {
                var placeholderType = m.Groups["type"].Value;
                var propName = m.Groups["name"].Value;
                return m.Value.Replace($"public {placeholderType} {propName}", $"public ContentBlock {propName}");
            });
        }

        return code;
    }

    private static HashSet<string> FindJsonPropertyNamesReferencing(JsonSchema schema, string targetDefName)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var def in schema.Definitions.Values)
        {
            var props = def?.ActualSchema?.Properties;
            if (props is null)
                continue;

            foreach (var (jsonPropName, prop) in props)
            {
                if (ReferencesDefinition(prop, targetDefName))
                    names.Add(jsonPropName);
            }
        }

        return names;
    }

    private static bool ReferencesDefinition(JsonSchemaProperty prop, string targetDefName)
    {
        // NJsonSchema may represent unions/refs via Reference directly or via AllOf entries.
        if (RefName(prop.Reference) == targetDefName) return true;
        if (RefName(prop.ActualSchema?.Reference) == targetDefName) return true;

        var allOf = prop.ActualSchema?.AllOf;
        if (allOf is not null)
        {
            foreach (var s in allOf)
            {
                if (RefName(s.Reference) == targetDefName) return true;
                if (RefName(s.ActualSchema?.Reference) == targetDefName) return true;
            }
        }

        return false;
    }

    private static string? RefName(JsonSchema? schema)
    {
        // NJsonSchema stores definition keys under schema.Definitions, and referenced schemas often have Title set.
        return schema?.Title;
    }
}
