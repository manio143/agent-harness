using System.Collections.Immutable;
using System.Text.Json;
using Agent.Acp.Acp;
using Agent.Acp.Schema;
using Agent.Harness.Persistence;
using Agent.Harness.TitleGeneration;

using MeaiIChatClient = Microsoft.Extensions.AI.IChatClient;

namespace Agent.Harness.Acp;

/// <summary>
/// ACP session agent backed by the harness.
///
/// Responsibility split:
/// - Server: hosts ACP transport + wires concrete <see cref="MeaiIChatClient"/> implementation.
/// - Harness: owns turn orchestration (observed -> reduce -> effects -> observed), including MEAI model calls.
///
/// Invariant: ACP publishes committed-only events.
/// </summary>
public sealed class HarnessAcpSessionAgent : IAcpSessionAgent
{
    private static ImmutableArray<ToolDefinition> ApplyToolAllowlist(ImmutableArray<ToolDefinition> tools, string allowlist)
    {
        return allowlist switch
        {
            _ => tools,
        };
    }

    private static List<SessionConfigOption> BuildConfigOptions(string allowlist)
    {
        var category = new Category();
        category.AdditionalProperties["name"] = "_tools";

        return new List<SessionConfigOption>
        {
            new()
            {
                Id = "tool_allowlist",
                Name = "Tool allowlist",
                Description = "Restrict which tools are declared to the model (permission boundary).",
                Category = category,
                Type = SessionConfigOptionType.Select,
                CurrentValue = allowlist,
                Options = new SessionConfigSelectOptions
                {
                    new() { Value = "all", Name = "All tools" },
                },
            },
        };
    }

    private readonly string _sessionId;
    private readonly IAcpClientCaller _client;
    private readonly MeaiIChatClient _chat;
    private readonly Func<string, MeaiIChatClient> _chatByModel;
    private readonly string _quickWorkModel;
    private readonly Func<string, bool>? _isKnownModel;
    private readonly IAcpSessionEvents _events;
    private readonly CoreOptions _coreOptions;
    private readonly AcpPublishOptions _publishOptions;
    private readonly ISessionStore _store;
    private readonly IMcpToolInvoker _mcp;
    private readonly bool _logLlmPrompts;
    private readonly bool _logObservedEvents;
    private readonly string? _modelCatalogSystemPrompt;

    private SessionState _state;
    private string _toolAllowlist = "all";

    private string LoadToolAllowlistFromStoreOrDefault()
    {
        var committed = _store.LoadCommitted(_sessionId);
        var last = committed.OfType<SessionConfigOptionSet>()
            .Where(e => e.ConfigId == "tool_allowlist")
            .Select(e => e.Value)
            .LastOrDefault();
        return string.IsNullOrWhiteSpace(last) ? "all" : last;
    }

    // Threading engine (long-lived per HarnessAcpSessionAgent instance when backing store supports threads).
    private readonly Agent.Harness.Threads.IThreadStore? _threadStore;
    private readonly Agent.Harness.Threads.ThreadManager? _threads;
    private readonly Agent.Harness.Threads.ThreadOrchestrator? _orchestrator;

    private readonly Dictionary<string, IAcpToolCall> _toolCalls = new();

    public HarnessAcpSessionAgent(
        string sessionId,
        IAcpClientCaller client,
        MeaiIChatClient chat,
        Func<string, MeaiIChatClient> chatByModel,
        string quickWorkModel,
        IAcpSessionEvents events,
        CoreOptions coreOptions,
        AcpPublishOptions publishOptions,
        ISessionStore store,
        SessionState initialState,
        IMcpToolInvoker? mcp = null,
        bool logLlmPrompts = false,
        bool logObservedEvents = false,
        Func<string, bool>? isKnownModel = null,
        string? modelCatalogSystemPrompt = null)
    {
        _sessionId = sessionId;
        _client = client;
        _chat = chat;
        _chatByModel = chatByModel;
        _quickWorkModel = quickWorkModel;
        _isKnownModel = isKnownModel;
        _events = events;
        _coreOptions = coreOptions;
        _publishOptions = publishOptions;
        _store = store;
        _state = initialState;
        _mcp = mcp ?? NullMcpToolInvoker.Instance;
        _logLlmPrompts = logLlmPrompts;
        _logObservedEvents = logObservedEvents;
        _modelCatalogSystemPrompt = modelCatalogSystemPrompt;

        // Tools are loaded once per session.
        // Tool catalog is the source of truth for what the LLM may call.
        // Per DECISIONS-tool-calling-mvp: populate it using negotiated client capabilities only.
        // Anything present in the catalog must be runnable.

        // Always-available harness-internal tools.
        var internalTools = ImmutableArray.Create<ToolDefinition>(ToolSchemas.ReportIntent);

        // Thread tools are runnable only when backed by a thread store (JsonlSessionStore).
        if (_store is JsonlSessionStore)
        {
            internalTools = ClientToolCatalog.Merge(internalTools, ImmutableArray.Create(
                ToolSchemas.ThreadList,
                ToolSchemas.ThreadConfig,
                ToolSchemas.ThreadStart,
                ToolSchemas.ThreadSend,
                ToolSchemas.ThreadRead));
        }

        // Capability-gated built-ins derived from negotiated client capabilities.
        var builtins = ClientToolCatalog.BuildBuiltins(_client.ClientCapabilities);

        // Restore session config (survives reconnect via committed events).
        _toolAllowlist = LoadToolAllowlistFromStoreOrDefault();

        // Merge: (pre-existing tools e.g. MCP) + internal + builtins
        _state = _state with { Tools = ClientToolCatalog.Merge(_state.Tools, ClientToolCatalog.Merge(internalTools, builtins)) };

        // Apply any session config-based tool restrictions.
        _state = _state with { Tools = ApplyToolAllowlist(_state.Tools, _toolAllowlist) };

        // Initialize thread engine once per session agent.
        if (_store is JsonlSessionStore jsonl)
        {
            _threadStore = new Agent.Harness.Threads.JsonlThreadStore(jsonl.RootDir);
            _threads = new Agent.Harness.Threads.ThreadManager(_sessionId, _threadStore);

            _orchestrator = new Agent.Harness.Threads.ThreadOrchestrator(
                _sessionId,
                _client,
                _chat,
                _chatByModel,
                _quickWorkModel,
                _mcp,
                _coreOptions,
                logLlmPrompts: _logLlmPrompts,
                _store,
                _threadStore,
                _threads,
                isKnownModel: _isKnownModel,
                modelCatalogSystemPrompt: _modelCatalogSystemPrompt);

            // Catalog == runnable/permission surface, and must be consistent across all threads.
            _orchestrator.InitializeToolCatalog(_state.Tools);
        }
    }

    public Task<SetSessionConfigOptionResponse>? SetSessionConfigOptionAsync(SetSessionConfigOptionRequest request, CancellationToken cancellationToken)
    {
        if (request.ConfigId == "tool_allowlist")
        {
            _toolAllowlist = request.Value;

            // Persist config so reconnect/load sees it.
            _store.AppendCommitted(_sessionId, new SessionConfigOptionSet("tool_allowlist", _toolAllowlist));

            // Update tool catalog immediately: permission boundary == declared tools.
            _state = _state with { Tools = ApplyToolAllowlist(_state.Tools, _toolAllowlist) };
            _orchestrator?.SetToolCatalog(_state.Tools);

            return Task.FromResult(new SetSessionConfigOptionResponse
            {
                ConfigOptions = BuildConfigOptions(_toolAllowlist),
            });
        }

        // Unsupported option.
        throw new Agent.Acp.Acp.AcpJsonRpcException(-32602, $"Unknown session config option: {request.ConfigId}");
    }

    public async Task<PromptResponse> PromptAsync(PromptRequest request, IAcpPromptTurn turn, CancellationToken cancellationToken)
    {
        // Ensure no dangling tool calls from a previous turn.
        if (turn.ToolCalls.ActiveToolCallIds.Count > 0)
        {
            await turn.ToolCalls.CancelAllAsync(cancellationToken).ConfigureAwait(false);
            _toolCalls.Clear();
        }

        // Tool catalog is loaded once per session agent (constructor).
        var userText = ExtractUserText(request);

        // ACP slash command: /set-model <friendlyName>
        // Sets inference model for the main thread without involving the LLM.
        if (TryParseSetModelCommand(userText, out var requestedModel))
        {
            if (string.IsNullOrWhiteSpace(requestedModel))
                throw new Agent.Acp.Acp.AcpJsonRpcException(-32602, "Usage: /set-model <modelName>");

            // Validation: allow literal "default" plus any known friendly model.
            var known = string.Equals(requestedModel, "default", StringComparison.OrdinalIgnoreCase)
                || _isKnownModel?.Invoke(requestedModel) == true;

            if (!known)
            {
                await _events.SendSessionUpdateAsync(new AgentMessageChunk
                {
                    Content = new Agent.Acp.Schema.TextContent { Text = $"Unknown model: {requestedModel}." },
                }, cancellationToken).ConfigureAwait(false);

                return new PromptResponse { StopReason = StopReason.EndTurn };
            }

            // Persist via normal observed->reduce->commit path.
            // NOTE: We intentionally run a direct "single turn" runner for the main thread here,
            // even when the threaded orchestrator is enabled, because /set-model is a local command
            // and we want deterministic persistence (no queue scheduling dependency).
            if (_threadStore is not null && _orchestrator is not null)
            {
                // Threaded mode: go through the standard observed -> reduce -> commit pipeline.
                IEventSink cmdPersist = new Agent.Harness.Threads.MainThreadEventSink(_sessionId, _threadStore, _store, logObserved: _logObservedEvents);
                var cmdSink = new AcpProjectingEventSink(
                    cmdPersist,
                    _coreOptions,
                    _publishOptions,
                    execute: (e, ct) => ExecuteEmissionAsync(e, turn, ct));

                await _orchestrator.ObserveAsync(
                    Agent.Harness.Threads.ThreadIds.Main,
                    new ObservedSetModel(Agent.Harness.Threads.ThreadIds.Main, requestedModel),
                    cancellationToken).ConfigureAwait(false);

                await _orchestrator.RunUntilQuiescentAsync(
                    sinkFactory: tid => tid == Agent.Harness.Threads.ThreadIds.Main ? cmdSink : null,
                    cancellationToken).ConfigureAwait(false);

                _state = _state with
                {
                    Committed = _threadStore.LoadCommittedEvents(_sessionId, Agent.Harness.Threads.ThreadIds.Main),
                    Buffer = TurnBuffer.Empty,
                };
            }
            else
            {
                static async IAsyncEnumerable<ObservedChatEvent> ObserveSetModel(string model)
                {
                    yield return new ObservedSetModel(Agent.Harness.Threads.ThreadIds.Main, model);
                    await Task.CompletedTask;
                }

                var cmdTitleGen = new SessionTitleGenerator(_chatByModel(_quickWorkModel));
                var cmdSessionCwd = _store.TryLoadMetadata(_sessionId)?.Cwd;

                var cmdEffects = new AcpEffectExecutor(
                    _sessionId,
                    _client,
                    _chat,
                    chatByModel: _chatByModel,
                    isKnownModel: _isKnownModel,
                    _mcp,
                    _logLlmPrompts,
                    sessionCwd: cmdSessionCwd,
                    store: _store,
                    modelCatalogSystemPrompt: _modelCatalogSystemPrompt,
                    threadTools: _orchestrator,
                    observer: _orchestrator,
                    lifecycle: _orchestrator,
                    scheduler: _orchestrator,
                    threadId: Agent.Harness.Threads.ThreadIds.Main);

                var cmdRunner = new SessionRunner(_coreOptions, cmdTitleGen, cmdEffects);
                var cmdSink = new AcpProjectingEventSink(
                    new Agent.Harness.Persistence.JsonlEventSink(_sessionId, _store, logObserved: _logObservedEvents),
                    _coreOptions,
                    _publishOptions,
                    execute: (e, ct) => ExecuteEmissionAsync(e, turn, ct));

                var result = await cmdRunner.RunTurnAsync(Agent.Harness.Threads.ThreadIds.Main, _state, ObserveSetModel(requestedModel), cancellationToken, sink: cmdSink).ConfigureAwait(false);
                _state = result.Next;
            }

            await _events.SendSessionUpdateAsync(new AgentMessageChunk
            {
                Content = new Agent.Acp.Schema.TextContent { Text = $"Inference model set to: {requestedModel}." },
            }, cancellationToken).ConfigureAwait(false);

            return new PromptResponse { StopReason = StopReason.EndTurn };
        }

        static async IAsyncEnumerable<ObservedChatEvent> ObservePromptAndWake(string text)
        {
            // Universal intake: user prompt enters the main thread inbox as an observed event.
            yield return Agent.Harness.Threads.ThreadInboxArrivals.UserPrompt(
                threadId: Agent.Harness.Threads.ThreadIds.Main,
                text: text,
                source: "user");

            // Turn boundary wake: gives the reducer a chance to dequeue+promote inbox items
            // into first-class prompt events before the next CallModel.
            yield return new ObservedWakeModel(Agent.Harness.Threads.ThreadIds.Main);
        }

        var titleGen = new SessionTitleGenerator(_chatByModel(_quickWorkModel));
        var sessionCwd = _store.TryLoadMetadata(_sessionId)?.Cwd;

        var effects = new AcpEffectExecutor(
            _sessionId,
            _client,
            _chat,
            chatByModel: _chatByModel,
            isKnownModel: _isKnownModel,
            _mcp,
            _logLlmPrompts,
            sessionCwd: sessionCwd,
            store: _store,
            modelCatalogSystemPrompt: _modelCatalogSystemPrompt,
            threadTools: _orchestrator,
            observer: _orchestrator,
            lifecycle: _orchestrator,
            scheduler: _orchestrator,
            threadId: Agent.Harness.Threads.ThreadIds.Main);
        var runner = new SessionRunner(_coreOptions, titleGen, effects);

        // Main thread is just another thread: persist committed events into the thread store when available.
        IEventSink persist;
        if (_threadStore is not null)
        {
            persist = new Agent.Harness.Threads.MainThreadEventSink(_sessionId, _threadStore, _store, logObserved: _logObservedEvents);
        }
        else
        {
            persist = new Agent.Harness.Persistence.JsonlEventSink(_sessionId, _store, logObserved: _logObservedEvents);
        }

        var sink = new AcpProjectingEventSink(
            persist,
            _coreOptions,
            _publishOptions,
            execute: (e, ct) => ExecuteEmissionAsync(e, turn, ct));

        // If threads aren't enabled (e.g. InMemory session store), fall back to the single-thread runner.
        if (_orchestrator is null || _threadStore is null)
        {
            var result = await runner.RunTurnAsync(Agent.Harness.Threads.ThreadIds.Main, _state, ObservePromptAndWake(userText), cancellationToken, sink: sink).ConfigureAwait(false);
            _state = result.Next;
            return new PromptResponse { StopReason = StopReason.EndTurn };
        }

        // Single processing loop: delegate all thread execution (main + children) to the thread orchestrator.
        // Main thread is just another thread; the only special-casing is projection/publishing to ACP.


        // Express user prompt as universal inbox arrival.
        await _orchestrator.ObserveAsync(
            Agent.Harness.Threads.ThreadIds.Main,
            Agent.Harness.Threads.ThreadInboxArrivals.UserPrompt(
                threadId: Agent.Harness.Threads.ThreadIds.Main,
                text: userText,
                source: "acp"),
            cancellationToken).ConfigureAwait(false);

        // Drain to quiescence in ONE orchestrator loop.
        await _orchestrator.RunUntilQuiescentAsync(
            sinkFactory: tid => tid == Agent.Harness.Threads.ThreadIds.Main ? sink : null,
            cancellationToken).ConfigureAwait(false);

        // ACP layer state is a view/cache.
        // Source of truth for committed history is the thread store; tool catalog remains our permission boundary.
        _state = _state with
        {
            Committed = _threadStore.LoadCommittedEvents(_sessionId, Agent.Harness.Threads.ThreadIds.Main),
            Buffer = TurnBuffer.Empty,
        };
        return new PromptResponse { StopReason = StopReason.EndTurn };
    }

    private async ValueTask ExecuteEmissionAsync(AcpEmission e, IAcpPromptTurn turn, CancellationToken cancellationToken)
    {
        switch (e)
        {
            case AcpSendAgentMessageChunk m:
                await _events.SendSessionUpdateAsync(new AgentMessageChunk
                {
                    Content = new Agent.Acp.Schema.TextContent { Text = m.Text },
                }, cancellationToken).ConfigureAwait(false);
                break;

            case AcpSendAgentThoughtChunk t:
                await _events.SendSessionUpdateAsync(new AgentThoughtChunk
                {
                    Content = new Agent.Acp.Schema.TextContent { Text = t.Text },
                }, cancellationToken).ConfigureAwait(false);
                break;

            case AcpToolCallStart s:
                _toolCalls[s.ToolId] = GetOrStart(turn, s.ToolId, title: s.Title);
                break;

            case AcpToolCallInProgress ip:
                if (_toolCalls.TryGetValue(ip.ToolId, out var inProgress))
                    await inProgress.InProgressAsync(cancellationToken).ConfigureAwait(false);
                break;

            case AcpToolCallAddText u:
                if (_toolCalls.TryGetValue(u.ToolId, out var updateCall))
                {
                    await updateCall.AddContentAsync(new ToolCallContentContent
                    {
                        Content = new Agent.Acp.Schema.TextContent { Text = u.Text },
                    }, cancellationToken).ConfigureAwait(false);
                }
                break;

            case AcpToolCallCompleted done:
                if (_toolCalls.TryGetValue(done.ToolId, out var completed))
                {
                    await completed.CompletedAsync(cancellationToken, rawOutput: done.RawOutput).ConfigureAwait(false);
                    _toolCalls.Remove(done.ToolId);
                }
                break;

            case AcpToolCallFailed failed:
                if (_toolCalls.TryGetValue(failed.ToolId, out var failedCall))
                {
                    await failedCall.FailedAsync(failed.Message, cancellationToken).ConfigureAwait(false);
                    _toolCalls.Remove(failed.ToolId);
                }
                break;

            case AcpToolCallCancelled cancelled:
                if (_toolCalls.TryGetValue(cancelled.ToolId, out var cancelledCall))
                {
                    await cancelledCall.CancelledAsync(cancellationToken).ConfigureAwait(false);
                    _toolCalls.Remove(cancelled.ToolId);
                }
                break;

            case AcpSendCustomUpdate u:
                await _events.SendSessionUpdateAsync(new Dictionary<string, object?>(u.Payload), cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private static IAcpToolCall GetOrStart(IAcpPromptTurn turn, string toolId, string title)
    {
        return turn.ToolCalls.Start(toolId, title, ToolKindClassifier.ForToolName(title));
    }

    private static bool TryParseSetModelCommand(string text, out string? model)
    {
        model = null;

        if (string.IsNullOrWhiteSpace(text)) return false;

        // Minimal parser: allow leading whitespace, exact command token, then argument.
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("/set-model", StringComparison.Ordinal)) return false;

        // Accept "/set-model" or "/set-model   x".
        var rest = trimmed["/set-model".Length..].Trim();
        model = rest;
        return true;
    }

    private static string ExtractUserText(PromptRequest request)
    {
        // Per ACP contract, prompt is an ordered list of content blocks.
        // Harness core currently expects a single textual user input, so we project supported
        // baseline blocks into a stable string form.
        //
        // Design grounding:
        // - Baseline prompt support includes Text + ResourceLink (validated in AcpAgentServer).
        // - We must not silently drop user input due to block ordering.

        var parts = new List<string>();

        foreach (var block in request.Prompt)
        {
            switch (block)
            {
                case Agent.Acp.Schema.TextContent t when !string.IsNullOrWhiteSpace(t.Text):
                    parts.Add(t.Text);
                    break;

                case Agent.Acp.Schema.ResourceLink r:
                {
                    var name = string.IsNullOrWhiteSpace(r.Name) ? "resource" : r.Name;
                    var title = string.IsNullOrWhiteSpace(r.Title) ? null : r.Title;
                    var desc = string.IsNullOrWhiteSpace(r.Description) ? null : r.Description;

                    // Keep the projection intentionally simple + stable for the LLM.
                    var line = title is not null
                        ? $"[resource_link name=\"{name}\" title=\"{title}\" uri=\"{r.Uri}\"]"
                        : $"[resource_link name=\"{name}\" uri=\"{r.Uri}\"]";

                    parts.Add(line);
                    if (desc is not null)
                        parts.Add($"[resource_description] {desc}");
                    break;
                }

                // UnknownContentBlock is allowed by schema for forward-compat.
                // For now: ignore (do not inject unreadable junk into the user prompt).
                default:
                    break;
            }
        }

        return string.Join("\n", parts);
    }
}
