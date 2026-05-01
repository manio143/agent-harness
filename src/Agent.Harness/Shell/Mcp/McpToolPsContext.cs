using System.Collections;
using System.Collections.Immutable;
using System.Text.Json;
using Agent.Harness.Acp;

namespace Agent.Harness.Shell.Mcp;

/// <summary>
/// PowerShell-visible MCP tool invocation context.
/// Stored as $global:__mcpCtx in the runspace.
/// </summary>
public sealed class McpToolPsContext
{
    private readonly IMcpToolInvoker _mcp;
    private readonly ImmutableHashSet<string> _allowedToolNames;

    public McpToolPsContext(IMcpToolInvoker mcp, ImmutableHashSet<string> allowedToolNames)
    {
        _mcp = mcp ?? NullMcpToolInvoker.Instance;
        _allowedToolNames = allowedToolNames;
    }

    public object? Invoke(string server, string name, Hashtable args, bool rawOutput)
    {
        var toolName = $"{server}__{name}";

        if (!_allowedToolNames.Contains(toolName) || !_mcp.CanInvoke(toolName))
            throw new InvalidOperationException($"mcp_tool_not_allowed:{toolName}");

        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry kv in args)
        {
            if (kv.Key is null) continue;
            dict[kv.Key.ToString() ?? ""] = kv.Value;
        }

        var toolId = "pwsh_" + Guid.NewGuid().ToString("N");
        var payload = _mcp.InvokeAsync(toolId, toolName, dict, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        if (rawOutput)
            return payload.GetRawText();

        return ConvertJson(payload);
    }

    private static object? ConvertJson(JsonElement el)
    {
        return el.ValueKind switch
        {
            JsonValueKind.Object => ConvertObject(el),
            JsonValueKind.Array => ConvertArray(el),
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Undefined => null,
            _ => el.GetRawText(),
        };
    }

    private static Hashtable ConvertObject(JsonElement obj)
    {
        var ht = new Hashtable(StringComparer.OrdinalIgnoreCase);
        foreach (var p in obj.EnumerateObject())
            ht[p.Name] = ConvertJson(p.Value);
        return ht;
    }

    private static object[] ConvertArray(JsonElement arr)
        => arr.EnumerateArray().Select(ConvertJson).Select(x => (object?)x).ToArray()!;
}
