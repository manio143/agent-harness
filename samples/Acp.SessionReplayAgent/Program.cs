using Agent.Acp.Acp;
using Agent.Acp.Schema;
using Agent.Acp.Transport;

// ACP sample agent that supports session/list + session/load and emits a deterministic
// "replay" update immediately after loading a session.
// Used by Avalonia client UI end-to-end tests to catch "load session hangs / no transcript" regressions.

var server = new AcpAgentServer(new SessionReplayFactory());

await using var transport = new LineDelimitedStreamTransport(
    input: Console.OpenStandardInput(),
    output: Console.OpenStandardOutput(),
    name: "stdio");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

await server.RunAsync(transport, cts.Token);

internal sealed class SessionReplayFactory : IAcpAgentFactory
{
    public Task<InitializeResponse> InitializeAsync(InitializeRequest request, CancellationToken cancellationToken)
    {
        return Task.FromResult(new InitializeResponse
        {
            ProtocolVersion = request.ProtocolVersion,
            AgentInfo = new AgentInfo
            {
                AdditionalProperties = new Dictionary<string, object>
                {
                    ["name"] = "acp-session-replay-agent",
                    ["version"] = "0.0.1",
                },
            },
            AgentCapabilities = new AgentCapabilities
            {
                PromptCapabilities = new PromptCapabilities(),
                SessionCapabilities = new SessionCapabilities
                {
                    List = new List(),
                },
                LoadSession = true,
            },
            AuthMethods = new List<AuthMethod>(),
        });
    }

    public Task<NewSessionResponse> NewSessionAsync(NewSessionRequest request, CancellationToken cancellationToken)
    {
        return Task.FromResult(new NewSessionResponse
        {
            SessionId = Guid.NewGuid().ToString(),
            Modes = null,
            ConfigOptions = null,
        });
    }

    public Task<ListSessionsResponse>? ListSessionsAsync(ListSessionsRequest request, CancellationToken cancellationToken)
    {
        return Task.FromResult(new ListSessionsResponse
        {
            Sessions = new List<SessionInfo>
            {
                new SessionInfo
                {
                    Cwd = request.Cwd ?? "/",
                    SessionId = "session-001",
                    Title = "Replayed session",
                    UpdatedAt = "2026-05-03T20:00:00Z",
                },
            },
        });
    }

    public Task<LoadSessionResponse>? LoadSessionAsync(LoadSessionRequest request, CancellationToken cancellationToken)
    {
        // Just acknowledge; the "replay" is emitted immediately after CreateSessionAgent.
        return Task.FromResult(new LoadSessionResponse
        {
            Modes = null,
            ConfigOptions = null,
        });
    }

    public IAcpSessionAgent CreateSessionAgent(string sessionId, IAcpClientCaller client, IAcpSessionEvents events)
        => new ReplayThenNoopSessionAgent(sessionId, events);

    private sealed class ReplayThenNoopSessionAgent : IAcpSessionAgent
    {
        private readonly string _sessionId;
        private readonly IAcpSessionEvents _events;

        public ReplayThenNoopSessionAgent(string sessionId, IAcpSessionEvents events)
        {
            _sessionId = sessionId;
            _events = events;

            // Simulate server replay by publishing a deterministic update as soon as the session is created.
            _ = Task.Run(async () =>
            {
                try
                {
                    await _events.SendSessionUpdateAsync(
                        new AgentMessageChunk { Content = new TextContent { Text = $"replay:{_sessionId}" } },
                        CancellationToken.None);
                }
                catch
                {
                    // Ignore; this is just a test sample.
                }
            });
        }

        public async Task<PromptResponse> PromptAsync(PromptRequest request, IAcpPromptTurn turn, CancellationToken cancellationToken)
        {
            await _events.SendSessionUpdateAsync(
                new AgentMessageChunk { Content = new TextContent { Text = "done" } },
                cancellationToken);

            return new PromptResponse { StopReason = StopReason.EndTurn };
        }

        public Task<SetSessionConfigOptionResponse>? SetSessionConfigOptionAsync(SetSessionConfigOptionRequest request, CancellationToken cancellationToken)
            => null;
    }
}
