using System.Text;
using NJsonSchema;
using NJsonSchema.CodeGeneration.CSharp;
using Agent.Acp.TypeGen;

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

code = CodegenPostProcessor.PostProcessGeneratedCode(schema, code);

Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
await File.WriteAllTextAsync(outFile, code, Encoding.UTF8);

Console.WriteLine($"Generated {outFile}");
return 0;

