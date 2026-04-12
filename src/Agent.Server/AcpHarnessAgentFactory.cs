using Agent.Acp.Acp;
using Agent.Acp.Schema;
using Agent.Harness;
using Agent.Harness.Acp;
using Microsoft.Extensions.AI;

using HarnessChatMessage = Agent.Harness.ChatMessage;
using HarnessChatRole = Agent.Harness.ChatRole;
using MeaiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using MeaiChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Agent.Server;

public sealed class AcpHarnessAgentFactory : IAcpAgentFactory, Agent.Acp.Acp.IAcpSessionReplayProvider
{
    private readonly Microsoft.Extensions.AI.IChatClient _chat;
    private readonly AgentServerOptions _options;
    private readonly Agent.Harness.Persistence.ISessionStore _store;

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

    public Task<NewSessionResponse> NewSessionAsync(NewSessionRequest request, CancellationToken cancellationToken)
    {
        var sessionId = Guid.NewGuid().ToString();
        _store.CreateNew(sessionId, new Agent.Harness.Persistence.SessionMetadata(
            SessionId: sessionId,
            Cwd: request.Cwd,
            Title: null,
            CreatedAtIso: DateTimeOffset.UtcNow.ToString("O"),
            UpdatedAtIso: DateTimeOffset.UtcNow.ToString("O")));

        return Task.FromResult(new NewSessionResponse
        {
            SessionId = sessionId,
            Modes = null,
            ConfigOptions = new List<SessionConfigOption>(),
        });
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
            : new SessionState(committed, TurnBuffer.Empty);

        return new MeaiAcpSessionAgent(sessionId, _chat, events, coreOptions, publishOptions, _store, initial);
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
                        Content = new TextContent { Text = u.Text },
                    }, cancellationToken).ConfigureAwait(false);
                    break;

                case AssistantMessage a:
                    await events.SendSessionUpdateAsync(new AgentMessageChunk
                    {
                        Content = new TextContent { Text = a.Text },
                    }, cancellationToken).ConfigureAwait(false);
                    break;

                // Deltas are omitted from replay because we have the final committed messages.
                // Reasoning is also omitted unless we explicitly decide to persist/replay it.
            }
        }
    }

    private sealed class MeaiAcpSessionAgent : IAcpSessionAgent
    {
        private readonly string _sessionId;
        private readonly Microsoft.Extensions.AI.IChatClient _chat;
        private readonly IAcpSessionEvents _events;
        private readonly CoreOptions _coreOptions;
        private readonly AcpPublishOptions _publishOptions;
        private readonly Agent.Harness.Persistence.ISessionStore _store;

        private SessionState _state;

        public MeaiAcpSessionAgent(
            string sessionId,
            Microsoft.Extensions.AI.IChatClient chat,
            IAcpSessionEvents events,
            CoreOptions coreOptions,
            AcpPublishOptions publishOptions,
            Agent.Harness.Persistence.ISessionStore store,
            SessionState initialState)
        {
            _sessionId = sessionId;
            _chat = chat;
            _events = events;
            _coreOptions = coreOptions;
            _publishOptions = publishOptions;
            _store = store;
            _state = initialState;
        }

        public Task<PromptResponse> PromptAsync(PromptRequest request, IAcpPromptTurn turn, CancellationToken cancellationToken)
        {
            // Build an observed stream consisting of:
            // - the user's message
            // - the model's streaming updates (assistant text + optional reasoning)
            async IAsyncEnumerable<ObservedChatEvent> Observed(PromptRequest req)
            {
                var userText = ExtractUserText(req);
                yield return new ObservedUserMessage(userText);

                // Render history for the model from committed events.
                var rendered = Core.RenderPrompt(_state);
                var meaiMessages = rendered
                    .Select(m => new MeaiChatMessage(m.Role switch
                    {
                        HarnessChatRole.User => MeaiChatRole.User,
                        HarnessChatRole.Assistant => MeaiChatRole.Assistant,
                        _ => MeaiChatRole.System,
                    }, m.Text))
                    .ToList();

                var updates = _chat.GetStreamingResponseAsync(meaiMessages, cancellationToken: cancellationToken);

                await foreach (var o in MeaiObservedEventSource.FromStreamingResponse(updates, cancellationToken))
                    yield return o;
            }

            return RunTurnAsync(request, Observed(request), cancellationToken);
        }

        private async Task<PromptResponse> RunTurnAsync(
            PromptRequest request,
            IAsyncEnumerable<ObservedChatEvent> observed,
            CancellationToken cancellationToken)
        {
            await foreach (var committed in TurnRunner.RunAsync(
                _state,
                observed,
                options: _coreOptions,
                onState: s => _state = s,
                cancellationToken: cancellationToken))
            {
                _store.AppendCommitted(_sessionId, committed);

                switch (committed)
                {
                    case AssistantMessage a:
                        if (!_coreOptions.CommitAssistantTextDeltas)
                        {
                            await _events.SendSessionUpdateAsync(new AgentMessageChunk
                            {
                                Content = new Agent.Acp.Schema.TextContent { Text = a.Text },
                            }, cancellationToken).ConfigureAwait(false);
                        }
                        break;

                    case AssistantTextDelta d:
                        await _events.SendSessionUpdateAsync(new AgentMessageChunk
                        {
                            Content = new Agent.Acp.Schema.TextContent { Text = d.TextDelta },
                        }, cancellationToken).ConfigureAwait(false);
                        break;

                    case ReasoningTextDelta r when _publishOptions.PublishReasoning:
                        await _events.SendSessionUpdateAsync(new AgentThoughtChunk
                        {
                            Content = new Agent.Acp.Schema.TextContent { Text = r.TextDelta },
                        }, cancellationToken).ConfigureAwait(false);
                        break;
                }
            }

            return new PromptResponse { StopReason = StopReason.EndTurn };
        }

        private static string ExtractUserText(PromptRequest request)
        {
            var first = request.Prompt.FirstOrDefault();
            if (first is Agent.Acp.Schema.TextContent t) return t.Text;
            return "";
        }
    }
}
