using System.Text.Json;
using Agent.Acp.Protocol;
using Agent.Acp.Schema;
using Agent.Harness.Persistence;

namespace Agent.Server;

public static class McpSessionPersistence
{
    public static McpServer NormalizeForPersistence(McpServer server)
    {
        // If server already has a stdio wrapper, keep it.
        if (server.AdditionalProperties.ContainsKey("stdio"))
            return server;

        // If server contains a flattened stdio config (command/args/env/name), wrap it.
        if (server.AdditionalProperties.ContainsKey("command"))
        {
            var stdio = new Dictionary<string, object?>();

            if (server.AdditionalProperties.TryGetValue("name", out var name)) stdio["name"] = name;
            if (server.AdditionalProperties.TryGetValue("command", out var cmd)) stdio["command"] = cmd;
            if (server.AdditionalProperties.TryGetValue("args", out var args)) stdio["args"] = args;
            if (server.AdditionalProperties.TryGetValue("env", out var env)) stdio["env"] = env;

            return new McpServer
            {
                AdditionalProperties = new Dictionary<string, object>
                {
                    ["stdio"] = stdio,
                }
            };
        }

        return server;
    }

    public static void PersistServers(ISessionStore store, string sessionId, string cwd, string sessionsDir, ICollection<McpServer> servers)
    {
        if (servers.Count == 0)
            return;

        var rootDir = (store as JsonlSessionStore)?.RootDir ?? Path.GetFullPath(Path.Combine(cwd, sessionsDir));
        var mcpConfigPath = Path.Combine(rootDir, sessionId, "mcpServers.json");

        // Normalize persisted config to ACP schema shape: { stdio: { name, command, args, env } }
        // Some clients (e.g. acpx) may send a flattened stdio config; we persist the wrapped form so
        // rehydrate + discovery remain stable.
        var normalized = servers.Select(NormalizeForPersistence).ToList();
        var json = JsonSerializer.Serialize(normalized, AcpJson.Options);
        File.WriteAllText(mcpConfigPath, json);
    }

    public static void TryAppendError(ISessionStore store, string sessionId, string phase, Exception ex)
    {
        try
        {
            if (store is not JsonlSessionStore js)
                return;

            var sessionDir = Path.Combine(js.RootDir, sessionId);
            Directory.CreateDirectory(sessionDir);

            var path = Path.Combine(sessionDir, "mcp.errors.jsonl");
            var line = JsonSerializer.Serialize(new
            {
                phase,
                message = ex.Message,
                exception = ex.GetType().FullName,
                stack = ex.ToString(),
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

            File.AppendAllText(path, line + "\n");
        }
        catch
        {
            // best-effort only
        }
    }
}
