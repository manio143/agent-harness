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

public sealed class AcpHarnessAgentFactory : IAcpAgentFactory
{
    private readonly Microsoft.Extensions.AI.IChatClient _chat;
    private readonly AgentServerOptions _options;
    private readonly SessionStore _store;

    public AcpHarnessAgentFactory(Microsoft.Extensions.AI.IChatClient chat, AgentServerOptions options, SessionStore store)
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
        _store.CreateNew(sessionId);

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
            return Task.FromResult(new LoadSessionResponse { ConfigOptions = new List<SessionConfigOption>(), Modes = null });

        return Task.FromResult(new LoadSessionResponse
        {
            Modes = null,
            ConfigOptions = new List<SessionConfigOption>(),
        });
    }

    public Task<ListSessionsResponse>? ListSessionsAsync(ListSessionsRequest request, CancellationToken cancellationToken)
    {
        var cwd = Path.GetFullPath(Directory.GetCurrentDirectory());

        var sessions = _store.ListSessions()
            .Select(id => new SessionInfo { SessionId = id, Cwd = cwd })
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

    private sealed class MeaiAcpSessionAgent : IAcpSessionAgent
    {
        private readonly string _sessionId;
        private readonly Microsoft.Extensions.AI.IChatClient _chat;
        private readonly IAcpSessionEvents _events;
        private readonly CoreOptions _coreOptions;
        private readonly AcpPublishOptions _publishOptions;
        private readonly SessionStore _store;

        private SessionState _state;

        public MeaiAcpSessionAgent(
            string sessionId,
            Microsoft.Extensions.AI.IChatClient chat,
            IAcpSessionEvents events,
            CoreOptions coreOptions,
            AcpPublishOptions publishOptions,
            SessionStore store,
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
                _store.Append(_sessionId, committed);

                switch (committed)
                {
                    case AssistantMessageAdded a:
                        if (!_coreOptions.CommitAssistantTextDeltas)
                        {
                            await _events.SendSessionUpdateAsync(new AgentMessageChunk
                            {
                                Content = new Agent.Acp.Schema.TextContent { Text = a.Text },
                            }, cancellationToken).ConfigureAwait(false);
                        }
                        break;

                    case AssistantMessageDeltaAdded d:
                        await _events.SendSessionUpdateAsync(new AgentMessageChunk
                        {
                            Content = new Agent.Acp.Schema.TextContent { Text = d.TextDelta },
                        }, cancellationToken).ConfigureAwait(false);
                        break;

                    case ReasoningDeltaAdded r when _publishOptions.PublishReasoning:
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
