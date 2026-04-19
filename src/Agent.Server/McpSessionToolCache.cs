using System.Collections.Immutable;
using System.Text.Json;
using Agent.Acp.Protocol;
using Agent.Acp.Schema;
using Agent.Harness;
using Agent.Harness.Acp;
using Agent.Harness.Persistence;

namespace Agent.Server;

/// <summary>
/// Caches per-session MCP tool discovery results.
///
/// The harness factory uses this to:
/// - eagerly rehydrate persisted MCP config during session/load
/// - avoid re-discovery on subsequent agent creation / prompts
/// </summary>
public sealed class McpSessionToolCache
{
    private readonly IMcpDiscovery _discovery;

    private readonly Dictionary<string, (ImmutableArray<ToolDefinition> Tools, IMcpToolInvoker Invoker)> _cache = new();

    public McpSessionToolCache(IMcpDiscovery discovery)
    {
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
    }

    public bool TryGet(string sessionId, out (ImmutableArray<ToolDefinition> Tools, IMcpToolInvoker Invoker) discovered)
    {
        lock (_cache)
            return _cache.TryGetValue(sessionId, out discovered);
    }

    public void Set(string sessionId, (ImmutableArray<ToolDefinition> Tools, IMcpToolInvoker Invoker) discovered)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentNullException(nameof(sessionId));

        lock (_cache)
            _cache[sessionId] = discovered;
    }

    public async Task EnsureDiscoveredOnLoadAsync(ISessionStore store, string sessionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentNullException(nameof(sessionId));

        // If already discovered (cached), nothing to do.
        lock (_cache)
        {
            if (_cache.ContainsKey(sessionId))
                return;
        }

        // Only JsonlSessionStore persists MCP config.
        if (store is not JsonlSessionStore js)
            return;

        var mcpConfigPath = Path.Combine(js.RootDir, sessionId, "mcpServers.json");
        if (!File.Exists(mcpConfigPath))
            return;

        var json = await File.ReadAllTextAsync(mcpConfigPath, cancellationToken).ConfigureAwait(false);
        var servers = JsonSerializer.Deserialize<List<McpServer>>(json, AcpJson.Options) ?? new List<McpServer>();
        if (servers.Count == 0)
            return;

        var meta = store.TryLoadMetadata(sessionId);
        var cwd = meta?.Cwd ?? Directory.GetCurrentDirectory();

        var req = new NewSessionRequest
        {
            Cwd = cwd,
            McpServers = servers,
        };

        var discovered = await _discovery.DiscoverAsync(req, cancellationToken).ConfigureAwait(false);

        lock (_cache)
        {
            _cache[sessionId] = discovered;
        }
    }
}
