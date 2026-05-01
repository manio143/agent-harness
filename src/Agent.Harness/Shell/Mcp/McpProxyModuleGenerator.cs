using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Agent.Acp.Schema;

namespace Agent.Harness.Shell.Mcp;

public static class McpProxyModuleGenerator
{
    public static IReadOnlyDictionary<string, string> Generate(
        IEnumerable<ToolDefinition> offeredTools,
        ImmutableHashSet<string> verbs)
    {
        // Only consider MCP tools of shape: {server}__{tool}
        var mcpTools = offeredTools
            .Select(t => (Tool: t, Parsed: ParseMcpToolName(t.Name)))
            .Where(x => x.Parsed is not null)
            .Select(x => (x.Tool, Parsed: x.Parsed!))
            .ToArray();

        // First pass: compute base cmdlet names (no server prefix).
        var baseNames = mcpTools
            .Select(t => new
            {
                Tool = t.Tool,
                Server = t.Parsed.Server,
                ToolName = t.Parsed.Tool,
                Base = McpCmdletNameMapper.Map(t.Parsed.Tool, verbs)
            })
            .ToArray();

        // Detect conflicts across servers.
        var conflicts = baseNames
            .GroupBy(x => x.Base.CmdletName, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(x => x.Server).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .Select(g => g.Key)
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

        // Generate per-server scripts.
        var byServer = baseNames
            .GroupBy(x => x.Server, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in byServer)
        {
            var server = kvp.Key;
            var tools = kvp.Value;

            var sb = new StringBuilder();
            sb.AppendLine("# Generated MCP proxy module");
            sb.AppendLine("Set-StrictMode -Version Latest");
            sb.AppendLine();


            foreach (var x in tools)
            {
                var mapping = x.Base;
                var cmdlet = mapping.CmdletName;

                if (conflicts.Contains(mapping.CmdletName))
                {
                    // Prefix server after verb.
                    var serverPascal = McpCmdletNameMapper.ToPascal(server.Split('-', '_'));
                    cmdlet = $"{mapping.Verb}-{serverPascal}{mapping.Noun}";
                }

                sb.AppendLine(GenerateFunction(server, x.Tool, x.ToolName, cmdlet));
                sb.AppendLine();
            }

            result[server] = sb.ToString();
        }

        return result;
    }

    private static string GenerateFunction(string server, ToolDefinition t, string toolName, string cmdletName)
    {
        var schema = t.InputSchema;
        var param = BuildParamBlock(schema);
        var binder = BuildArgsBinder(schema);

        var sb = new StringBuilder();
        sb.AppendLine("<#");
        sb.AppendLine(".SYNOPSIS");
        sb.AppendLine($"MCP tool proxy for {Escape(server)}__{Escape(toolName)}");
        if (!string.IsNullOrWhiteSpace(t.Description))
            sb.AppendLine(Escape(t.Description));

        // Emit parameter descriptions from schema for Get-Help.
        if (t.InputSchema.ValueKind == JsonValueKind.Object
            && t.InputSchema.TryGetProperty("properties", out var props)
            && props.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in props.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.Object) continue;
                if (!p.Value.TryGetProperty("description", out var d) || d.ValueKind != JsonValueKind.String) continue;
                var desc = d.GetString();
                if (string.IsNullOrWhiteSpace(desc)) continue;

                sb.AppendLine(".PARAMETER " + ToPsParamName(p.Name));
                sb.AppendLine(Escape(desc));
            }
        }

        sb.AppendLine("#>");
        sb.AppendLine($"function {cmdletName} {{");
        sb.AppendLine("  [CmdletBinding()] ");
        sb.AppendLine(param);
        sb.AppendLine("  $args = @{} ");
        sb.AppendLine(binder);
        sb.AppendLine("  if ($null -ne $RawArgs) { foreach ($k in $RawArgs.Keys) { if (-not $args.ContainsKey($k)) { $args[$k] = $RawArgs[$k] } } }");
        sb.AppendLine($"  return Invoke-McpTool -Server '{Escape(server)}' -Name '{Escape(toolName)}' -Args $args -RawOutput:$RawOutput");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string BuildParamBlock(JsonElement schema)
    {
        // Very small subset: object properties to parameters.
        var parameters = new List<(string Name, string PsType, bool Mandatory)>();

        if (schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
        {
            var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (schema.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in req.EnumerateArray())
                    if (r.ValueKind == JsonValueKind.String)
                        required.Add(r.GetString() ?? "");
            }

            foreach (var prop in props.EnumerateObject())
            {
                var name = prop.Name;
                var mandatory = required.Contains(name);
                var psType = MapType(prop.Value, mandatory);
                parameters.Add((name, psType, mandatory));
            }
        }

        // Always add escape hatches.
        parameters.Add(("RawArgs", "hashtable", false));
        parameters.Add(("RawOutput", "switch", false));

        var sb = new StringBuilder();
        sb.AppendLine("  param(");

        for (var i = 0; i < parameters.Count; i++)
        {
            var p = parameters[i];
            var comma = i == parameters.Count - 1 ? "" : ",";
            if (p.Name is "RawOutput")
            {
                sb.AppendLine($"    [switch]${p.Name}{comma}");
                continue;
            }

            if (p.Name is "RawArgs")
            {
                sb.AppendLine($"    [hashtable]${p.Name}{comma}");
                continue;
            }

            var mandatory = p.Mandatory ? "[Parameter(Mandatory=$true)]" : "";
            var validateSet = p is { Name: not "RawArgs", Mandatory: true } && TryGetValidateSet(schema, p.Name, out var vs)
                ? $"[ValidateSet({vs})]"
                : "";
            var psName = ToPsParamName(p.Name);
            sb.AppendLine($"    {mandatory}{validateSet}[{p.PsType}]${psName}{comma}");
        }

        sb.AppendLine("  )");
        return sb.ToString();
    }

    private static string BuildArgsBinder(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object)
            return "";

        var sb = new StringBuilder();
        foreach (var prop in props.EnumerateObject())
        {
            var psName = ToPsParamName(prop.Name);
            var jsonName = prop.Name;
            sb.AppendLine($"  if ($PSBoundParameters.Keys -contains '{psName}') {{ $args['{Escape(jsonName)}'] = ${psName} }}");
        }

        return sb.ToString();
    }

    private static string MapType(JsonElement schema, bool mandatory)
    {
        // Strict when representable; only be loose when schema is complex/ambiguous.
        // NOTE: even optional parameters can have strict types in PowerShell.
        if (schema.ValueKind != JsonValueKind.Object)
            return mandatory ? "string" : "object";

        // Complex schema shapes we don't model yet.
        if (schema.TryGetProperty("oneOf", out _)
            || schema.TryGetProperty("anyOf", out _)
            || schema.TryGetProperty("allOf", out _))
            return "object";

        if (!schema.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.String)
            return mandatory ? "string" : "object";

        var type = t.GetString();
        return type switch
        {
            "string" => "string",
            "integer" => "int",
            "number" => "double",
            // Optional bool is representable as [bool]$X = $null (unset) vs $false (bound).
            "boolean" => "bool",
            "array" => MapArrayType(schema),
            "object" => "hashtable",
            _ => mandatory ? "string" : "object",
        };
    }

    private static string MapArrayType(JsonElement schema)
    {
        if (schema.ValueKind == JsonValueKind.Object
            && schema.TryGetProperty("items", out var items)
            && items.ValueKind == JsonValueKind.Object
            && items.TryGetProperty("type", out var it)
            && it.ValueKind == JsonValueKind.String)
        {
            return it.GetString() switch
            {
                "string" => "string[]",
                "integer" => "int[]",
                "number" => "double[]",
                "boolean" => "bool[]",
                "object" => "hashtable[]",
                _ => "object[]",
            };
        }

        return "object[]";
    }

    private static bool TryGetValidateSet(JsonElement schema, string propertyName, out string values)
    {
        values = "";

        if (schema.ValueKind != JsonValueKind.Object) return false;
        if (!schema.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object) return false;
        if (!props.TryGetProperty(propertyName, out var propSchema) || propSchema.ValueKind != JsonValueKind.Object) return false;

        if (!propSchema.TryGetProperty("enum", out var en) || en.ValueKind != JsonValueKind.Array) return false;
        if (!propSchema.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.String || t.GetString() != "string") return false;

        var items = new List<string>();
        foreach (var v in en.EnumerateArray())
        {
            if (v.ValueKind != JsonValueKind.String) return false;
            var s = v.GetString() ?? "";
            items.Add($"'{Escape(s)}'");
        }

        values = string.Join(",", items);
        return items.Count > 0;
    }

    private static string ToPsParamName(string jsonName)
    {
        // Convert snake_case to PascalCase to match cmdlet vibe.
        var parts = jsonName.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return McpCmdletNameMapper.ToPascal(parts);
    }

    private static string Escape(string s) => s.Replace("'", "''");

    private sealed record McpToolName(string Server, string Tool);

    private static McpToolName? ParseMcpToolName(string name)
    {
        var idx = name.IndexOf("__", StringComparison.Ordinal);
        if (idx <= 0 || idx >= name.Length - 2)
            return null;

        var server = name[..idx];
        var tool = name[(idx + 2)..];

        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(tool))
            return null;

        return new McpToolName(server, tool);
    }
}
