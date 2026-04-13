using System.Collections.Immutable;
using System.Text.Json;
using Agent.Acp.Acp;
using Agent.Acp.Protocol;
using Agent.Acp.Schema;
using Agent.Harness;
using Agent.Harness.Acp;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;

namespace Agent.Server;

internal static class McpDiscovery
{
    public static async Task<(ImmutableArray<ToolDefinition> Tools, IMcpToolInvoker Invoker)> DiscoverAsync(
        NewSessionRequest request,
        CancellationToken cancellationToken)
    {
        var tools = ImmutableArray.CreateBuilder<ToolDefinition>();
        var invokerTools = new Dictionary<string, McpClientTool>(StringComparer.Ordinal);

        for (var i = 0; i < request.McpServers.Count; i++)
        {
            var server = request.McpServers.ElementAt(i);

            if (server.AdditionalProperties.ContainsKey("http") || server.AdditionalProperties.ContainsKey("sse"))
                throw new AcpJsonRpcException(-32602, "unsupported mcp transport");

            if (!server.AdditionalProperties.TryGetValue("stdio", out var stdioObj))
                continue;

            var stdio = JsonSerializer.SerializeToElement(stdioObj, AcpJson.Options);

            var command = stdio.TryGetProperty("command", out var cmd) && cmd.ValueKind == JsonValueKind.String
                ? cmd.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(command))
                throw new AcpJsonRpcException(-32602, "invalid mcp stdio config: missing command");

            var args = new List<string>();
            if (stdio.TryGetProperty("args", out var argsEl) && argsEl.ValueKind == JsonValueKind.Array)
                args.AddRange(argsEl.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.String).Select(a => a.GetString()!));

            // Prefer explicit name if present; otherwise derive from command.
            var name = stdio.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                ? nameEl.GetString()!
                : command;

            var serverId = SnakeCase.Normalize(name);
            if (string.IsNullOrWhiteSpace(serverId))
                serverId = $"mcp_{i + 1}";

            var env = new Dictionary<string, string?>(StringComparer.Ordinal);
            if (stdio.TryGetProperty("env", out var envEl) && envEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in envEl.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object) continue;
                    if (!e.TryGetProperty("key", out var k) || k.ValueKind != JsonValueKind.String) continue;
                    if (!e.TryGetProperty("value", out var v) || v.ValueKind != JsonValueKind.String) continue;
                    env[k.GetString() ?? ""] = v.GetString() ?? "";
                }
            }

            var transportOptions = new StdioClientTransportOptions
            {
                Name = serverId,
                Command = command,
                WorkingDirectory = request.Cwd,
                EnvironmentVariables = env,
            };
            foreach (var a in args)
                transportOptions.Arguments.Add(a);

            var clientTransport = new StdioClientTransport(transportOptions);
            var mcp = await McpClient.CreateAsync(clientTransport, new McpClientOptions(), NullLoggerFactory.Instance, cancellationToken)
                .ConfigureAwait(false);

            var listed = await mcp.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

            foreach (var t in listed)
            {
                var toolId = SnakeCase.Normalize(t.Name);
                if (string.IsNullOrWhiteSpace(toolId))
                    continue;

                var exposed = $"{serverId}__{toolId}";

                tools.Add(new ToolDefinition(exposed, t.Description, t.JsonSchema));
                invokerTools[exposed] = t;
            }
        }

        return (tools.ToImmutable(), new SdkMcpToolInvoker(invokerTools));
    }

    private sealed class SdkMcpToolInvoker : IMcpToolInvoker
    {
        private readonly IReadOnlyDictionary<string, McpClientTool> _tools;

        public SdkMcpToolInvoker(IReadOnlyDictionary<string, McpClientTool> tools) => _tools = tools;

        public bool CanInvoke(string toolName) => _tools.ContainsKey(toolName);

        public async Task<JsonElement> InvokeAsync(string toolId, string toolName, object args, CancellationToken cancellationToken)
        {
            var t = _tools[toolName];

            var je = args is JsonElement e ? e : JsonSerializer.SerializeToElement(args);
            var dict = je.ValueKind == JsonValueKind.Object
                ? je.EnumerateObject().ToDictionary(
                    p => p.Name,
                    p => (object?)JsonSerializer.Deserialize<object?>(p.Value.GetRawText()))
                : new Dictionary<string, object?>();

            var result = await t.CallAsync(dict, cancellationToken: cancellationToken).ConfigureAwait(false);

            return JsonSerializer.SerializeToElement(new
            {
                isError = result.IsError,
                structuredContent = result.StructuredContent,
                content = result.Content,
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
    }
}
