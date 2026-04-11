using System.Text.RegularExpressions;
using NJsonSchema;

namespace Agent.Acp.TypeGen;

public static class CodegenPostProcessor
{
    /// <summary>
    /// Post-process NJsonSchema-generated C# to remove known placeholder types deterministically.
    ///
    /// Today: NJsonSchema emits placeholder types (e.g. Content1) for some union-heavy references.
    /// We patch properties marked as JSON "content" to use the intended union base type ContentBlock.
    ///
    /// This is deterministic and schema-aware (only runs if ContentBlock exists).
    /// </summary>
    public static string PostProcessGeneratedCode(JsonSchema schema, string code)
    {
        if (!schema.Definitions.TryGetValue("ContentBlock", out _))
            return code;

        // Replace placeholder Content{n} property types for any property decorated as "content".
        // Example:
        //   [JsonPropertyName("content")]
        //   public Content1 Content { get; set; }
        // becomes:
        //   public ContentBlock Content { get; set; }
        var re = new Regex(
            "\\[System\\.Text\\.Json\\.Serialization\\.JsonPropertyName\\(\\\"content\\\"\\)\\]\\s*\\r?\\n\\s*public\\s+(?<type>Content\\d+)\\s+(?<name>\\w+)\\b",
            RegexOptions.Compiled);

        return re.Replace(code, m =>
        {
            var placeholderType = m.Groups["type"].Value;
            var propName = m.Groups["name"].Value;
            return m.Value.Replace($"public {placeholderType} {propName}", $"public ContentBlock {propName}");
        });
    }
}
