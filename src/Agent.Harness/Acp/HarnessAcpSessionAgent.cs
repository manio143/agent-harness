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
    private readonly string _sessionId;
    private readonly IAcpClientCaller _client;
    private readonly MeaiIChatClient _chat;
    private readonly IAcpSessionEvents _events;
    private readonly CoreOptions _coreOptions;
    private readonly AcpPublishOptions _publishOptions;
    private readonly ISessionStore _store;
    private readonly IMcpToolInvoker _mcp;
    private readonly bool _logLlmPrompts;
    private readonly bool _logObservedEvents;

    private SessionState _state;
    private readonly Dictionary<string, IAcpToolCall> _toolCalls = new();

    public HarnessAcpSessionAgent(
        string sessionId,
        IAcpClientCaller client,
        MeaiIChatClient chat,
        IAcpSessionEvents events,
        CoreOptions coreOptions,
        AcpPublishOptions publishOptions,
        ISessionStore store,
        SessionState initialState,
        IMcpToolInvoker? mcp = null,
        bool logLlmPrompts = false,
        bool logObservedEvents = false)
    {
        _sessionId = sessionId;
        _client = client;
        _chat = chat;
        _events = events;
        _coreOptions = coreOptions;
        _publishOptions = publishOptions;
        _store = store;
        _state = initialState;
        _mcp = mcp ?? NullMcpToolInvoker.Instance;
        _logLlmPrompts = logLlmPrompts;
        _logObservedEvents = logObservedEvents;
    }

    public async Task<PromptResponse> PromptAsync(PromptRequest request, IAcpPromptTurn turn, CancellationToken cancellationToken)
    {
        // Ensure no dangling tool calls from a previous turn.
        if (turn.ToolCalls.ActiveToolCallIds.Count > 0)
        {
            await turn.ToolCalls.CancelAllAsync(cancellationToken).ConfigureAwait(false);
            _toolCalls.Clear();
        }

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
                ToolSchemas.ThreadNew,
                ToolSchemas.ThreadFork,
                ToolSchemas.ThreadSend,
                ToolSchemas.ThreadRead));
        }

        // Capability-gated built-ins derived from negotiated client capabilities.
        var builtins = ClientToolCatalog.BuildBuiltins(_client.ClientCapabilities);

        // Merge: (pre-existing tools e.g. MCP) + internal + builtins
        _state = _state with { Tools = ClientToolCatalog.Merge(_state.Tools, ClientToolCatalog.Merge(internalTools, builtins)) };

        async IAsyncEnumerable<ObservedChatEvent> ObservedUserInput()
        {
            var userText = ExtractUserText(request);

            var now = DateTimeOffset.UtcNow.ToString("O");
            var envId = Agent.Harness.Threads.ThreadEnvelopes.NewEnvelopeId();

            // Universal intake: user prompt enters the main thread inbox as an observed event.
            yield return new ObservedInboxMessageArrived(
                ThreadId: Agent.Harness.Threads.ThreadIds.Main,
                Kind: Agent.Harness.Threads.ThreadInboxMessageKind.UserPrompt,
                Delivery: Agent.Harness.Threads.InboxDelivery.Immediate,
                EnvelopeId: envId,
                EnqueuedAtIso: now,
                Source: "user",
                SourceThreadId: null,
                Text: userText,
                Meta: null);

            // Turn boundary wake: gives the reducer a chance to dequeue+promote inbox items
            // into first-class prompt events before the next CallModel.
            yield return new ObservedWakeModel(Agent.Harness.Threads.ThreadIds.Main);
        }

        var titleGen = new SessionTitleGenerator(new Llm.MeaiTitleChatClientAdapter(_chat));
        var sessionCwd = _store.TryLoadMetadata(_sessionId)?.Cwd;

        // Thread layer: persisted alongside the session store when backed by JsonlSessionStore.
        Agent.Harness.Threads.ThreadManager? threads = null;
        Agent.Harness.Threads.ThreadOrchestrator? orchestrator = null;
        Agent.Harness.Threads.IThreadScheduler? scheduler = null;
        Agent.Harness.Threads.IThreadStore? threadStore = null;

        if (_store is JsonlSessionStore jsonl)
        {
            threadStore = new Agent.Harness.Threads.JsonlThreadStore(jsonl.RootDir);
            threads = new Agent.Harness.Threads.ThreadManager(_sessionId, threadStore);

            orchestrator = new Agent.Harness.Threads.ThreadOrchestrator(
                _sessionId,
                _client,
                _chat,
                _mcp,
                _coreOptions,
                _store,
                threadStore,
                threads);

            scheduler = orchestrator;
        }

        var effects = new AcpEffectExecutor(_sessionId, _client, _chat, _mcp, _logLlmPrompts, sessionCwd: sessionCwd, store: _store, threads: threads, scheduler: scheduler, threadId: Agent.Harness.Threads.ThreadIds.Main);
        var runner = new SessionRunner(_coreOptions, titleGen, effects);

        var persist = new Agent.Harness.Persistence.JsonlEventSink(_sessionId, _store, logObserved: _logObservedEvents);
        var sink = new AcpProjectingEventSink(
            persist,
            _coreOptions,
            _publishOptions,
            execute: (e, ct) => ExecuteEmissionAsync(e, turn, ct));

        var result = await runner.RunTurnAsync(_state, ObservedUserInput(), cancellationToken, sink: sink).ConfigureAwait(false);
        _state = result.Next;

        // Global quiescence: do not end the ACP turn until the main thread AND all child threads are idle.
        if (threads is not null)
        {
            for (var i = 0; i < 250; i++)
            {
                // Thread status is derived from committed turn markers (TurnStarted/TurnEnded).

                // Main enqueue wake: keep calling model while enqueue becomes deliverable.
                if (threads.HasDeliverableEnqueueNow(Agent.Harness.Threads.ThreadIds.Main))
                {
                    async IAsyncEnumerable<ObservedChatEvent> WakeObserved()
                    {
                        yield return new ObservedWakeModel(Agent.Harness.Threads.ThreadIds.Main);
                    }

                    var wake = await runner.RunTurnAsync(_state, WakeObserved(), cancellationToken, sink: sink).ConfigureAwait(false);
                    _state = wake.Next;
                    continue;
                }

                // Run child threads that were scheduled by tool calls / inbox delivery.
                if (orchestrator is not null && orchestrator.HasPendingWork)
                {
                    await orchestrator.RunUntilQuiescentAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                // Stable: main has no deliverable enqueue and orchestrator has no queued work.
                break;
            }
        }

        // Persistence + ACP presentation is incremental via the sink.

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
