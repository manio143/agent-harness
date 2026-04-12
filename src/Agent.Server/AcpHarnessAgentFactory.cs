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
            var sawAssistantMessage = false;

            await foreach (var committed in TurnRunner.RunAsync(
                _state,
                observed,
                options: _coreOptions,
                onState: s => _state = s,
                cancellationToken: cancellationToken))
            {
                _store.AppendCommitted(_sessionId, committed);

                if (committed is AssistantMessage)
                    sawAssistantMessage = true;

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

            // After first completed turn: generate a session title (metadata is a projection of committed events).
            var meta = _store.TryLoadMetadata(_sessionId);
            if (meta is not null && meta.Title is null && sawAssistantMessage)
            {
                var title = await GenerateTitleAsync(cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(title))
                {
                    var evt = new SessionTitleSet(title);
                    _store.AppendCommitted(_sessionId, evt);
                    _state = _state with { Committed = _state.Committed.Add(evt) };
                }
            }

            return new PromptResponse { StopReason = StopReason.EndTurn };
        }

        private async Task<string?> GenerateTitleAsync(CancellationToken cancellationToken)
        {
            const string systemPrompt = "You're a title generator based on the following conversation <conversation>...</conversation> you must output precisely one short line that contains a title for this conversation.";

            var conversation = Core.RenderPrompt(_state)
                .Select(m => $"{m.Role}: {m.Text}")
                .ToList();

            var user = "<conversation>\n" + string.Join("\n", conversation) + "\n</conversation>";

            var resp = await _chat.GetResponseAsync(
                [
                    new MeaiChatMessage(MeaiChatRole.System, systemPrompt),
                    new MeaiChatMessage(MeaiChatRole.User, user),
                ],
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var lastMsg = resp.Messages.LastOrDefault();
            var text = resp.Text
                ?? lastMsg?.Text
                ?? lastMsg?.Contents?.OfType<Microsoft.Extensions.AI.TextContent>().FirstOrDefault()?.Text;

            if (string.IsNullOrWhiteSpace(text)) return null;

            var line = text.Trim().Split('\n', '\r', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
            if (string.IsNullOrWhiteSpace(line)) return null;

            // Small safety clamp.
            return line.Length <= 80 ? line : line[..80];
        }

        private static string ExtractUserText(PromptRequest request)
        {
            var first = request.Prompt.FirstOrDefault();
            if (first is Agent.Acp.Schema.TextContent t) return t.Text;
            return "";
        }
    }
}
