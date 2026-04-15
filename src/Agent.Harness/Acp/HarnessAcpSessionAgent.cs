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

        // Capability-gated tool catalog: built-in tools are derived from negotiated client capabilities.
        // Merge into any pre-existing tools (e.g. MCP-discovered tools).
        var builtins = ClientToolCatalog.BuildBuiltins(_client.ClientCapabilities);
        _state = _state with { Tools = ClientToolCatalog.Merge(_state.Tools, builtins) };

        async IAsyncEnumerable<ObservedChatEvent> ObservedUserInput()
        {
            var userText = ExtractUserText(request);
            yield return new ObservedUserMessage(userText);
        }

        var titleGen = new SessionTitleGenerator(new Llm.MeaiTitleChatClientAdapter(_chat));
        var sessionCwd = _store.TryLoadMetadata(_sessionId)?.Cwd;

        // Thread layer: persisted alongside the session store when backed by JsonlSessionStore.
        Agent.Harness.Threads.ThreadManager? threads = null;
        if (_store is JsonlSessionStore jsonl)
        {
            var threadStore = new Agent.Harness.Threads.JsonlThreadStore(jsonl.RootDir);
            threads = new Agent.Harness.Threads.ThreadManager(_sessionId, threadStore);
        }

        var effects = new AcpEffectExecutor(_sessionId, _client, _chat, _mcp, _logLlmPrompts, sessionCwd: sessionCwd, store: _store, threads: threads);
        var runner = new SessionRunner(_coreOptions, titleGen, effects);

        var persist = new Agent.Harness.Persistence.JsonlEventSink(_sessionId, _store, logObserved: _logObservedEvents);
        var sink = new AcpProjectingEventSink(
            persist,
            _coreOptions,
            _publishOptions,
            execute: (e, ct) => ExecuteEmissionAsync(e, turn, ct));

        var result = await runner.RunTurnAsync(_state, ObservedUserInput(), cancellationToken, sink: sink).ConfigureAwait(false);
        _state = result.Next;

        // Enqueue delivery semantics: if the inbox contains enqueue messages at turn end,
        // we must immediately schedule another model call so the thread does not become idle.
        if (threads is not null)
        {
            for (var i = 0; i < 25; i++)
            {
                if (!threads.HasPendingEnqueue(Agent.Harness.Threads.ThreadIds.Main))
                    break;

                async IAsyncEnumerable<ObservedChatEvent> WakeObserved()
                {
                    yield return new ObservedWakeModel();
                }

                var wake = await runner.RunTurnAsync(_state, WakeObserved(), cancellationToken, sink: sink).ConfigureAwait(false);
                _state = wake.Next;
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
        }
    }

    private static IAcpToolCall GetOrStart(IAcpPromptTurn turn, string toolId, string title)
    {
        return turn.ToolCalls.Start(toolId, title, ToolKindClassifier.ForToolName(title));
    }

    private static string ExtractUserText(PromptRequest request)
    {
        var first = request.Prompt.FirstOrDefault();
        if (first is Agent.Acp.Schema.TextContent t) return t.Text;
        return "";
    }
}
