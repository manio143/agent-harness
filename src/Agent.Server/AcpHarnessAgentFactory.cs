using System.Collections.Immutable;
using System.Linq;
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
    private static SessionConfigOption ToolAllowlistConfigOption(string current)
    {
        // ACP Category is extension-data shaped.
        var category = new Category();
        category.AdditionalProperties["name"] = "_tools";

        return new SessionConfigOption
        {
            Id = "tool_allowlist",
            Name = "Tool allowlist",
            Description = "Restrict which tools are declared to the model (permission boundary).",
            Category = category,
            Type = SessionConfigOptionType.Select,
            CurrentValue = current,
            Options = new SessionConfigSelectOptions
            {
                new() { Value = "all", Name = "All tools" },
            },
        };
    }

    private static string BuildModelCatalogSystemPrompt(ModelCatalog catalog)
    {
        var names = catalog.Models.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var list = names.Length == 0 ? "(none)" : string.Join(", ", names);

        return $"Available inference models: {list}. Default: {catalog.DefaultModel}. Quick-work: {catalog.QuickWorkModel}.";
    }

    private readonly Microsoft.Extensions.AI.IChatClient _chat;
    private readonly IChatClientFactory _chatFactory;
    private readonly ModelCatalog _modelCatalog;
    private readonly AgentServerOptions _options;
    private readonly IMcpDiscovery _mcpDiscovery;
    private readonly McpSessionToolCache _mcpCache;

    // Session store is rooted at ACP client-provided CWD to align with acpx expectations.
    // We keep a per-session cache because subsequent calls like CreateSessionAgent only provide sessionId.
    private readonly Dictionary<string, ISessionStore> _storesBySessionId = new();


    public AcpHarnessAgentFactory(
        Microsoft.Extensions.AI.IChatClient chat,
        AgentServerOptions options,
        IMcpDiscovery? mcpDiscovery = null)
        : this(
            chat,
            new OpenAiChatClientFactory(ModelCatalog.FromOptions(options)),
            ModelCatalog.FromOptions(options),
            options,
            mcpDiscovery)
    {
    }

    public AcpHarnessAgentFactory(
        Microsoft.Extensions.AI.IChatClient chat,
        IChatClientFactory chatFactory,
        ModelCatalog modelCatalog,
        AgentServerOptions options,
        IMcpDiscovery? mcpDiscovery = null)
    {
        _chat = chat;
        _chatFactory = chatFactory;
        _modelCatalog = modelCatalog;
        _options = options;
        _mcpDiscovery = mcpDiscovery ?? new DefaultMcpDiscovery();
        _mcpCache = new McpSessionToolCache(_mcpDiscovery);
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

    private static McpServer NormalizeMcpServerForPersistence(McpServer server)
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

    private static void TryAppendMcpError(ISessionStore store, string sessionId, string phase, Exception ex)
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

            // Normalize persisted config to ACP schema shape: { stdio: { name, command, args, env } }
            // Some clients (e.g. acpx) may send a flattened stdio config; we persist the wrapped form so
            // rehydrate + discovery remain stable.
            var normalized = request.McpServers.Select(NormalizeMcpServerForPersistence).ToList();
            var json = JsonSerializer.Serialize(normalized, AcpJson.Options);
            File.WriteAllText(mcpConfigPath, json);
        }

        object? mcpErrors = null;

        // MCP discovery (ephemeral connections per session): connect and eagerly call tools/list.
        if (request.McpServers.Count > 0)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                var discovered = await _mcpDiscovery.DiscoverAsync(request, timeout.Token).ConfigureAwait(false);
                _mcpCache.Set(sessionId, discovered);
            }
            catch (Exception ex)
            {
                // Graceful degradation: session still created, but MCP tools unavailable.
                mcpErrors = new[]
                {
                    new { message = ex.Message },
                };

                TryAppendMcpError(store, sessionId, phase: "session_new", ex);
            }
        }

        var allowlist = "all";
        var resp = new NewSessionResponse
        {
            SessionId = sessionId,
            Modes = null,
            ConfigOptions = new List<SessionConfigOption>
            {
                ToolAllowlistConfigOption(current: allowlist),
            },
        };

        if (mcpErrors is not null)
            resp.AdditionalProperties["mcpErrors"] = mcpErrors;

        return resp;
    }

    public async Task<LoadSessionResponse> LoadSessionAsync(LoadSessionRequest request, CancellationToken cancellationToken)
    {
        var store = GetOrCreateSessionStore(request.SessionId, request.Cwd);

        if (!store.Exists(request.SessionId))
            throw new Agent.Acp.Acp.AcpJsonRpcException(-32602, $"Session not found: {request.SessionId}");

        // Rehydrate MCP config eagerly during session/load (async). This avoids any blocking
        // .GetResult() during CreateSessionAgent and keeps harness execution fully async.
        object? mcpErrors = null;

        try
        {
            await _mcpCache.EnsureDiscoveredOnLoadAsync(store, request.SessionId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            mcpErrors = new[] { new { message = ex.Message } };
            TryAppendMcpError(store, request.SessionId, phase: "session_load", ex);
        }

        var allowlist = store.LoadCommitted(request.SessionId)
            .OfType<Agent.Harness.SessionConfigOptionSet>()
            .Where(e => e.ConfigId == "tool_allowlist")
            .Select(e => e.Value)
            .LastOrDefault() ?? "all";

        var resp = new LoadSessionResponse
        {
            Modes = null,
            ConfigOptions = new List<SessionConfigOption>
            {
                ToolAllowlistConfigOption(current: allowlist),
            },
        };

        if (mcpErrors is not null)
            resp.AdditionalProperties["mcpErrors"] = mcpErrors;

        return resp;
    }


    public Task<ListSessionsResponse> ListSessionsAsync(ListSessionsRequest request, CancellationToken cancellationToken)
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

        // Main thread is just another thread: load committed from the thread store when available.
        ImmutableArray<SessionEvent> committed;

        if (store is JsonlSessionStore js)
        {
            var threadStore = new Agent.Harness.Threads.JsonlThreadStore(js.RootDir);
            committed = threadStore.LoadCommittedEvents(sessionId, Agent.Harness.Threads.ThreadIds.Main);
        }
        else
        {
            committed = store.LoadCommitted(sessionId);
        }

        var initial = committed.IsDefaultOrEmpty
            ? SessionState.Empty
            : new SessionState(committed, TurnBuffer.Empty, ImmutableArray<ToolDefinition>.Empty);

        var mcp = _mcpCache.TryGet(sessionId, out var v)
            ? v
            : (ImmutableArray<ToolDefinition>.Empty, NullMcpToolInvoker.Instance);


        // Merge MCP tools into the session state tool catalog (built-ins are merged later per client capabilities).
        initial = initial with { Tools = ClientToolCatalog.Merge(initial.Tools, mcp.Tools) };

        return new HarnessAcpSessionAgent(
            sessionId,
            client,
            _chat,
            _chatFactory.Get,
            _modelCatalog.QuickWorkModel,
            events,
            coreOptions,
            publishOptions,
            store,
            initial,
            mcp.Invoker,
            logLlmPrompts: _options.Logging.LogLlmPrompts,
            logObservedEvents: _options.Logging.LogObservedEvents,
            isKnownModel: _modelCatalog.IsKnownModel,
            modelCatalogSystemPrompt: BuildModelCatalogSystemPrompt(_modelCatalog));
    }

    public async Task ReplaySessionAsync(string sessionId, IAcpSessionEvents events, CancellationToken cancellationToken)
    {
        var store = TryGetSessionStore(sessionId);
        if (store is null)
            return;

        // Main thread is just another thread: replay committed from the main thread log when available.
        ImmutableArray<SessionEvent> committed;
        if (store is JsonlSessionStore js)
        {
            var threadStore = new Agent.Harness.Threads.JsonlThreadStore(js.RootDir);
            committed = threadStore.LoadCommittedEvents(sessionId, Agent.Harness.Threads.ThreadIds.Main);
        }
        else
        {
            committed = store.LoadCommitted(sessionId);
        }

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
