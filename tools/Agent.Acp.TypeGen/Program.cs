using System.Text;
using NJsonSchema;
using NJsonSchema.CodeGeneration.CSharp;

// Generates C# DTOs for ACP from the published JSON Schema.
//
// Why this exists:
// - ACP's schema.json is definition-heavy ($defs) and some CLI generators only emit the root type.
// - We want deterministic generation of all definitions.
//
// Usage (from repo root):
//   dotnet run --project tools/Agent.Acp.TypeGen -- schema/schema.json src/Agent.Acp/Generated/AcpTypes.g.cs Agent.Acp.Schema

if (args.Length < 3)
{
    Console.Error.WriteLine("Usage: <schemaPath> <outFile> <namespace>");
    return 2;
}

var schemaPath = args[0];
var outFile = args[1];
var @namespace = args[2];

var schemaJson = await File.ReadAllTextAsync(schemaPath, Encoding.UTF8);
var schema = await JsonSchema.FromJsonAsync(schemaJson);

Console.WriteLine($"Parsed schema. Definitions: {schema.Definitions.Count}");

var settings = new CSharpGeneratorSettings
{
    Namespace = @namespace,
    ClassStyle = CSharpClassStyle.Poco,
    GenerateDataAnnotations = false,
    GenerateDefaultValues = false,
};

settings.JsonLibrary = CSharpJsonLibrary.SystemTextJson;
settings.GenerateNullableReferenceTypes = true;

var generator = new CSharpGenerator(schema, settings);
var code = generator.GenerateFile();

code = PostProcessGeneratedCode(schema, code);

Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
await File.WriteAllTextAsync(outFile, code, Encoding.UTF8);

Console.WriteLine($"Generated {outFile}");
return 0;

static string PostProcessGeneratedCode(JsonSchema schema, string code)
{
    // NJsonSchema currently generates placeholder types (e.g. Content1) for some union references.
    // We apply a deterministic, schema-driven patch:
    // if a property is named "content" and its schema references the ContentBlock definition,
    // ensure the generated C# property type is ContentBlock.
    //
    // This avoids ad-hoc Python patching and keeps the pipeline regeneratable.

    // If ContentBlock doesn't exist, there's nothing to patch.
    if (!schema.Definitions.TryGetValue("ContentBlock", out var _))
        return code;

    // Patch any property with [JsonPropertyName("content")] where the type is a Content* placeholder.
    // We intentionally avoid renaming the property itself (just the type).
    var re = new System.Text.RegularExpressions.Regex(
        "\\[System\\.Text\\.Json\\.Serialization\\.JsonPropertyName\\(\\\"content\\\"\\)\\]\\s*\\r?\\n\\s*public\\s+(?<type>Content\\d+)\\s+(?<name>\\w+)\\b",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    code = re.Replace(code, m =>
    {
        var placeholderType = m.Groups["type"].Value;
        var propCSharpName = m.Groups["name"].Value;
        return m.Value.Replace($"public {placeholderType} {propCSharpName}", $"public ContentBlock {propCSharpName}");
    });

    return code;
}
