using System.Threading;
using Agent.Acp.Acp;
using Agent.Acp.Schema;
using Agent.Acp.Transport;

// ACP sample agent that supports session/list, but fails on the *second* list call.
// Used to exercise refresh error handling in the ACP Avalonia client.

var server = new AcpAgentServer(new FlakySessionListFactory());

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

internal sealed class FlakySessionListFactory : IAcpAgentFactory
{
    private int _listCalls;

    public Task<InitializeResponse> InitializeAsync(InitializeRequest request, CancellationToken cancellationToken)
    {
        return Task.FromResult(new InitializeResponse
        {
            ProtocolVersion = request.ProtocolVersion,
            AgentInfo = new AgentInfo
            {
                AdditionalProperties = new Dictionary<string, object>
                {
                    ["name"] = "acp-session-list-flaky-agent",
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
        // 1st call succeeds (connect flow), subsequent calls fail (Refresh button).
        var call = Interlocked.Increment(ref _listCalls);
        if (call >= 2)
            return null; // => server responds method-not-found (deterministic failure)

        return Task.FromResult(new ListSessionsResponse
        {
            Sessions = new List<SessionInfo>
            {
                new SessionInfo
                {
                    Cwd = request.Cwd ?? "/",
                    SessionId = "session-001",
                    Title = "First session",
                    UpdatedAt = "2026-05-03T20:00:00Z",
                },
            },
        });
    }

    public Task<LoadSessionResponse>? LoadSessionAsync(LoadSessionRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new LoadSessionResponse { Modes = null, ConfigOptions = null });

    public IAcpSessionAgent CreateSessionAgent(string sessionId, IAcpClientCaller client, IAcpSessionEvents events)
        => new NoopSessionAgent(events);

    private sealed class NoopSessionAgent : IAcpSessionAgent
    {
        private readonly IAcpSessionEvents _events;

        public NoopSessionAgent(IAcpSessionEvents events)
        {
            _events = events;
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
