using System.Collections.Immutable;
using System.Text.Json;
using Agent.Acp.Acp;
using Agent.Acp.Schema;
using Agent.Harness;
using Agent.Harness.Llm;
using Agent.Harness.Acp;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace Agent.Server;

public sealed class AcpHarnessAgentFactory : IAcpAgentFactory, Agent.Acp.Acp.IAcpSessionReplayProvider
{
    private readonly Microsoft.Extensions.AI.IChatClient _chat;
    private readonly AgentServerOptions _options;
    private readonly Agent.Harness.Persistence.ISessionStore _store;

    private readonly Dictionary<string, (ImmutableArray<ToolDefinition> Tools, IMcpToolInvoker Invoker)> _mcp = new();

    public AcpHarnessAgentFactory(Microsoft.Extensions.AI.IChatClient chat, AgentServerOptions options, Agent.Harness.Persistence.ISessionStore store)
    {
        _chat = chat;
        _options = options;
        _store = store;
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
        _store.CreateNew(sessionId, new Agent.Harness.Persistence.SessionMetadata(
            SessionId: sessionId,
            Cwd: request.Cwd,
            Title: null,
            CreatedAtIso: DateTimeOffset.UtcNow.ToString("O"),
            UpdatedAtIso: DateTimeOffset.UtcNow.ToString("O")));

        // Persist MCP config for reconnects (acpx may restart the agent process between commands and use session/load).
        if (request.McpServers.Count > 0)
        {
            var mcpConfigPath = Path.Combine(_store.RootDir, sessionId, "mcpServers.json");
            var json = JsonSerializer.Serialize(request.McpServers, AcpJson.Options);
            File.WriteAllText(mcpConfigPath, json);
        }

        // MCP discovery (ephemeral connections per session): connect and eagerly call tools/list.
        if (request.McpServers.Count > 0)
        {
            var discovered = await McpDiscovery.DiscoverAsync(request, cancellationToken).ConfigureAwait(false);
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
        if (!_store.Exists(request.SessionId))
            throw new Agent.Acp.Acp.AcpJsonRpcException(-32602, $"Session not found: {request.SessionId}");

        return Task.FromResult(new LoadSessionResponse
        {
            Modes = null,
            ConfigOptions = new List<SessionConfigOption>(),
        });
    }

    public Task<ListSessionsResponse>? ListSessionsAsync(ListSessionsRequest request, CancellationToken cancellationToken)
    {
        var cwd = Path.GetFullPath(Directory.GetCurrentDirectory());

        var sessions = _store.ListSessionIds()
            .Select(id =>
            {
                var meta = _store.TryLoadMetadata(id);
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
        var coreOptions = new CoreOptions(
            CommitAssistantTextDeltas: _options.Core.CommitAssistantTextDeltas,
            CommitReasoningTextDeltas: _options.Core.CommitReasoningTextDeltas);

        var publishOptions = new AcpPublishOptions(PublishReasoning: _options.Acp.PublishReasoning);

        var committed = _store.LoadCommitted(sessionId);
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
            var mcpConfigPath = Path.Combine(_store.RootDir, sessionId, "mcpServers.json");
            if (File.Exists(mcpConfigPath))
            {
                try
                {
                    var json = File.ReadAllText(mcpConfigPath);
                    var servers = JsonSerializer.Deserialize<List<McpServer>>(json, AcpJson.Options) ?? new List<McpServer>();
                    if (servers.Count > 0)
                    {
                        var req = new NewSessionRequest { Cwd = _store.TryLoadMetadata(sessionId)?.Cwd ?? "/", McpServers = servers };
                        mcp = McpDiscovery.DiscoverAsync(req, CancellationToken.None).GetAwaiter().GetResult();
                    }
                }
                catch
                {
                    // Best-effort: if MCP rehydrate fails, proceed without MCP.
                }
            }
        }

        // Merge MCP tools into the session state tool catalog (built-ins are merged later per client capabilities).
        initial = initial with { Tools = ClientToolCatalog.Merge(initial.Tools, mcp.Tools) };

        return new HarnessAcpSessionAgent(sessionId, client, _chat, events, coreOptions, publishOptions, _store, initial, mcp.Invoker, logLlmPrompts: _options.Logging.LogLlmPrompts);
    }

    public async Task ReplaySessionAsync(string sessionId, IAcpSessionEvents events, CancellationToken cancellationToken)
    {
        var committed = _store.LoadCommitted(sessionId);

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
