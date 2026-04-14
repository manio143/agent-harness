using System.Collections.Immutable;
using System.Text.Json;
using Agent.Acp.Acp;
using Agent.Acp.Protocol;
using Agent.Acp.Schema;
using Agent.Harness;
using Agent.Harness.Llm;
using Agent.Harness.Acp;
using Agent.Harness.Persistence;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace Agent.Server;

public sealed class AcpHarnessAgentFactory : IAcpAgentFactory, Agent.Acp.Acp.IAcpSessionReplayProvider
{
    private readonly Microsoft.Extensions.AI.IChatClient _chat;
    private readonly AgentServerOptions _options;
    private readonly IMcpDiscovery _mcpDiscovery;

    // Session store is rooted at ACP client-provided CWD to align with acpx expectations.
    // We keep a per-session cache because subsequent calls like CreateSessionAgent only provide sessionId.
    private readonly Dictionary<string, ISessionStore> _storesBySessionId = new();

    private readonly Dictionary<string, (ImmutableArray<ToolDefinition> Tools, IMcpToolInvoker Invoker)> _mcp = new();

    public AcpHarnessAgentFactory(Microsoft.Extensions.AI.IChatClient chat, AgentServerOptions options, IMcpDiscovery? mcpDiscovery = null)
    {
        _chat = chat;
        _options = options;
        _mcpDiscovery = mcpDiscovery ?? new DefaultMcpDiscovery();
    }

    private ISessionStore CreateStoreForCwd(string cwd)
    {
        var root = Path.GetFullPath(Path.Combine(cwd, _options.Sessions.Directory));
        return new JsonlSessionStore(root);
    }

    private ISessionStore GetOrCreateSessionStore(string sessionId, string cwd)
    {
        lock (_storesBySessionId)
        {
            if (_storesBySessionId.TryGetValue(sessionId, out var existing))
                return existing;

            var created = CreateStoreForCwd(cwd);
            _storesBySessionId[sessionId] = created;
            return created;
        }
    }

    private ISessionStore? TryGetSessionStore(string sessionId)
    {
        lock (_storesBySessionId)
        {
            return _storesBySessionId.TryGetValue(sessionId, out var store) ? store : null;
        }
    }

    public Task<InitializeResponse> InitializeAsync(InitializeRequest request, CancellationToken cancellationToken)
    {
        return Task.FromResult(new InitializeResponse
        {
            ProtocolVersion = request.ProtocolVersion,
            AgentInfo = new AgentInfo
            {
                AdditionalProperties = new Dictionary<string, object>
                {
                    ["name"] = "agent-server",
                    ["version"] = "0.0.0",
                },
            },
            AgentCapabilities = new AgentCapabilities
            {
                PromptCapabilities = new PromptCapabilities(),
                McpCapabilities = new McpCapabilities(),
                SessionCapabilities = new SessionCapabilities { List = new Agent.Acp.Schema.List() },
                LoadSession = true,
            },
            AuthMethods = new List<AuthMethod>(),
        });
    }

    public async Task<NewSessionResponse> NewSessionAsync(NewSessionRequest request, CancellationToken cancellationToken)
    {
        var sessionId = Guid.NewGuid().ToString();
        var store = GetOrCreateSessionStore(sessionId, request.Cwd);

        store.CreateNew(sessionId, new Agent.Harness.Persistence.SessionMetadata(
            SessionId: sessionId,
            Cwd: request.Cwd,
            Title: null,
            CreatedAtIso: DateTimeOffset.UtcNow.ToString("O"),
            UpdatedAtIso: DateTimeOffset.UtcNow.ToString("O")));

        // Persist MCP config for reconnects (acpx may restart the agent process between commands and use session/load).
        if (request.McpServers.Count > 0)
        {
            var rootDir = (store as JsonlSessionStore)?.RootDir ?? Path.GetFullPath(Path.Combine(request.Cwd, _options.Sessions.Directory));
            var mcpConfigPath = Path.Combine(rootDir, sessionId, "mcpServers.json");
            var json = JsonSerializer.Serialize(request.McpServers, AcpJson.Options);
            File.WriteAllText(mcpConfigPath, json);
        }

        // MCP discovery (ephemeral connections per session): connect and eagerly call tools/list.
        if (request.McpServers.Count > 0)
        {
            var discovered = await _mcpDiscovery.DiscoverAsync(request, cancellationToken).ConfigureAwait(false);
            lock (_mcp)
            {
                _mcp[sessionId] = discovered;
            }
        }

        return new NewSessionResponse
        {
            SessionId = sessionId,
            Modes = null,
            ConfigOptions = new List<SessionConfigOption>(),
        };
    }

    public Task<LoadSessionResponse>? LoadSessionAsync(LoadSessionRequest request, CancellationToken cancellationToken)
    {
        var store = GetOrCreateSessionStore(request.SessionId, request.Cwd);

        if (!store.Exists(request.SessionId))
            throw new Agent.Acp.Acp.AcpJsonRpcException(-32602, $"Session not found: {request.SessionId}");

        return Task.FromResult(new LoadSessionResponse
        {
            Modes = null,
            ConfigOptions = new List<SessionConfigOption>(),
        });
    }

    public Task<ListSessionsResponse>? ListSessionsAsync(ListSessionsRequest request, CancellationToken cancellationToken)
    {
        // List sessions relative to the agent process cwd. In practice, acpx runs the agent with `--cwd`
        // set to the workspace it cares about.
        var cwd = Path.GetFullPath(Directory.GetCurrentDirectory());
        var store = CreateStoreForCwd(cwd);

        var sessions = store.ListSessionIds()
            .Select(id =>
            {
                var meta = store.TryLoadMetadata(id);
                return new SessionInfo
                {
                    SessionId = id,
                    Cwd = meta?.Cwd ?? cwd,
                    Title = meta?.Title,
                    UpdatedAt = meta?.UpdatedAtIso,
                };
            })
            .ToList();

        return Task.FromResult(new ListSessionsResponse { Sessions = sessions });
    }

    public IAcpSessionAgent CreateSessionAgent(string sessionId, IAcpClientCaller client, IAcpSessionEvents events)
    {
        var store = TryGetSessionStore(sessionId);
        if (store is null)
        {
            // Best-effort fallback: try process cwd.
            var cwd = Path.GetFullPath(Directory.GetCurrentDirectory());
            store = CreateStoreForCwd(cwd);
            // NOTE: we intentionally do not cache this; session/load/session/new should provide authoritative cwd.
        }

        var coreOptions = new CoreOptions(
            CommitAssistantTextDeltas: _options.Core.CommitAssistantTextDeltas,
            CommitReasoningTextDeltas: _options.Core.CommitReasoningTextDeltas);

        var publishOptions = new AcpPublishOptions(PublishReasoning: _options.Acp.PublishReasoning);

        var committed = store.LoadCommitted(sessionId);
        var initial = committed.IsDefaultOrEmpty
            ? SessionState.Empty
            : new SessionState(committed, TurnBuffer.Empty, ImmutableArray<ToolDefinition>.Empty);

        (ImmutableArray<ToolDefinition> Tools, IMcpToolInvoker Invoker) mcp;
        lock (_mcp)
        {
            mcp = _mcp.TryGetValue(sessionId, out var v)
                ? v
                : (ImmutableArray<ToolDefinition>.Empty, NullMcpToolInvoker.Instance);
        }

        // If this is a fresh process and we only have session replay, attempt to rehydrate MCP config.
        if (mcp.Tools.IsDefaultOrEmpty)
        {
            var rootDir = (store as JsonlSessionStore)?.RootDir;
            if (rootDir is not null)
            {
                var mcpConfigPath = Path.Combine(rootDir, sessionId, "mcpServers.json");
                if (File.Exists(mcpConfigPath))
                {
                    try
                    {
                        var json = File.ReadAllText(mcpConfigPath);
                        var servers = JsonSerializer.Deserialize<List<McpServer>>(json, AcpJson.Options) ?? new List<McpServer>();
                        if (servers.Count > 0)
                        {
                            var metaCwd = store.TryLoadMetadata(sessionId)?.Cwd ?? "/";
                            var req = new NewSessionRequest { Cwd = metaCwd, McpServers = servers };
                            mcp = _mcpDiscovery.DiscoverAsync(req, CancellationToken.None).GetAwaiter().GetResult();
                        }
                    }
                    catch
                    {
                        // Best-effort: if MCP rehydrate fails, proceed without MCP.
                    }
                }
            }
        }

        // Merge MCP tools into the session state tool catalog (built-ins are merged later per client capabilities).
        initial = initial with { Tools = ClientToolCatalog.Merge(initial.Tools, mcp.Tools) };

        return new HarnessAcpSessionAgent(
            sessionId,
            client,
            _chat,
            events,
            coreOptions,
            publishOptions,
            store,
            initial,
            mcp.Invoker,
            logLlmPrompts: _options.Logging.LogLlmPrompts,
            logObservedEvents: _options.Logging.LogObservedEvents);
    }

    public async Task ReplaySessionAsync(string sessionId, IAcpSessionEvents events, CancellationToken cancellationToken)
    {
        var store = TryGetSessionStore(sessionId);
        if (store is null)
            return;

        var committed = store.LoadCommitted(sessionId);

        // Replay stable history: full user/assistant messages only.
        foreach (var evt in committed)
        {
            switch (evt)
            {
                case UserMessage u:
                    await events.SendSessionUpdateAsync(new UserMessageChunk
                    {
                        Content = new Agent.Acp.Schema.TextContent { Text = u.Text },
                    }, cancellationToken).ConfigureAwait(false);
                    break;

                case AssistantMessage a:
                    await events.SendSessionUpdateAsync(new AgentMessageChunk
                    {
                        Content = new Agent.Acp.Schema.TextContent { Text = a.Text },
                    }, cancellationToken).ConfigureAwait(false);
                    break;

                // Deltas are omitted from replay because we have the final committed messages.
                // Reasoning is replayed only when explicitly published.

                case ReasoningTextDelta r when _options.Acp.PublishReasoning:
                    await events.SendSessionUpdateAsync(new AgentThoughtChunk
                    {
                        Content = new Agent.Acp.Schema.TextContent { Text = r.TextDelta },
                    }, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
    }

}
