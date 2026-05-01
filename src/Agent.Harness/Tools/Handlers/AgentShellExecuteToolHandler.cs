using System.Collections.Immutable;
using System.Text.Json;
using Agent.Harness.Persistence;
using Agent.Harness.Shell;

namespace Agent.Harness.Tools.Handlers;

/// <summary>
/// Exposes an in-process PowerShell Core runspace to the model.
/// Tool name: agent_shell_execute
/// </summary>
public sealed class AgentShellExecuteToolHandler : IToolHandler, IDisposable
{
    public static ToolDefinition Definition { get; } = new(
        Name: "agent_shell_execute",
        Description: "Execute a PowerShell Core script inside an in-process, session-scoped sandbox working directory. The shell state (variables/functions) persists across calls within the same session/thread.",
        InputSchema: ParseSchema("""
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "script": { "type": "string", "description": "PowerShell script to execute" }
          },
          "required": ["script"]
        }
        """));

    private readonly string _sessionId;
    private readonly string _threadId;
    private readonly ISessionStore? _store;
    private readonly Agent.Acp.Acp.IAcpClientCaller? _client;
    private readonly string? _sessionCwd;

    private InProcessPowerShellSession? _ps;

    public AgentShellExecuteToolHandler(
        string sessionId,
        string threadId,
        ISessionStore? store,
        Agent.Acp.Acp.IAcpClientCaller? client = null,
        string? sessionCwd = null)
    {
        _sessionId = sessionId;
        _threadId = threadId;
        _store = store;
        _client = client;
        _sessionCwd = sessionCwd;
    }

    ToolDefinition IToolHandler.Definition => Definition;

    public Task<ImmutableArray<ObservedChatEvent>> ExecuteAsync(SessionState state, ExecuteToolCall tool, CancellationToken cancellationToken)
    {
        var args = Agent.Harness.Tools.ToolArgs.Normalize(tool.Args);
        var script = GetRequiredString(args, "script");

        // Lazily initialize to ensure the working dir exists.
        _ps ??= new InProcessPowerShellSession(
            workingDir: GetWorkingDir(),
            client: _client,
            sessionId: _client is null ? null : _sessionId,
            sessionCwd: _sessionCwd,
            store: _store);

        var result = _ps.Execute(script, cancellationToken);

        var json = JsonSerializer.SerializeToElement(new
        {
            ok = result.Success,
            stdout = result.Stdout,
            stderr = result.Stderr,
        });

        return Task.FromResult(ImmutableArray.Create<ObservedChatEvent>(
            new ObservedToolCallCompleted(tool.ToolId, json)));
    }

    private string GetWorkingDir()
    {
        // Prefer the jsonl store root so artifacts land alongside the session.
        if (_store is JsonlSessionStore jsonl)
        {
            // Shared working dir across threads (session-scoped), but execution state (runspace) is per-thread.
            var dir = Path.Combine(jsonl.RootDir, _sessionId, "pwsh_work");
            Directory.CreateDirectory(dir);
            return dir;
        }

        // Fallback: temp directory.
        var tmp = Path.Combine(Path.GetTempPath(), "agent", "sessions", _sessionId, "pwsh_work");
        Directory.CreateDirectory(tmp);
        return tmp;
    }

    private static string GetRequiredString(Dictionary<string, JsonElement> obj, string name)
    {
        if (!obj.TryGetValue(name, out var v) || v.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException($"missing_required:{name}");

        return v.GetString() ?? "";
    }

    private static JsonElement ParseSchema(string json)
        => JsonDocument.Parse(json).RootElement.Clone();

    public void Dispose()
    {
        _ps?.Dispose();
        _ps = null;
    }
}
