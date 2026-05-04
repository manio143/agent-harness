using System.Threading;
using Agent.Acp.Acp;
using Agent.Acp.Schema;
using Agent.Acp.Transport;

// ACP sample agent focused on deterministic streaming sequences.

var server = new AcpAgentServer(new StreamingFactory());

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

internal sealed class StreamingFactory : IAcpAgentFactory
{
    public Task<InitializeResponse> InitializeAsync(InitializeRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new InitializeResponse
        {
            ProtocolVersion = request.ProtocolVersion,
            AgentInfo = new AgentInfo
            {
                AdditionalProperties = new Dictionary<string, object>
                {
                    ["name"] = "acp-streaming-agent",
                    ["version"] = "0.0.1",
                },
            },
            AgentCapabilities = new AgentCapabilities
            {
                PromptCapabilities = new PromptCapabilities(),
                SessionCapabilities = new SessionCapabilities(),
                LoadSession = false,
            },
            AuthMethods = new List<AuthMethod>(),
        });

    public Task<NewSessionResponse> NewSessionAsync(NewSessionRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new NewSessionResponse
        {
            SessionId = Guid.NewGuid().ToString(),
            Modes = null,
            ConfigOptions = null,
        });

    public Task<ListSessionsResponse>? ListSessionsAsync(ListSessionsRequest request, CancellationToken cancellationToken) => null;

    public Task<LoadSessionResponse>? LoadSessionAsync(LoadSessionRequest request, CancellationToken cancellationToken) => null;

    public IAcpSessionAgent CreateSessionAgent(string sessionId, IAcpClientCaller client, IAcpSessionEvents events)
        => new StreamingSessionAgent(events);

    private sealed class StreamingSessionAgent : IAcpSessionAgent
    {
        private readonly IAcpSessionEvents _events;

        public StreamingSessionAgent(IAcpSessionEvents events)
        {
            _events = events;
        }

        public async Task<PromptResponse> PromptAsync(PromptRequest request, IAcpPromptTurn turn, CancellationToken cancellationToken)
        {
            // Message chunks that should coalesce into a single ChatText.
            await _events.SendSessionUpdateAsync(
                new AgentMessageChunk { Content = new TextContent { Text = "A" } },
                cancellationToken);
            await _events.SendSessionUpdateAsync(
                new AgentMessageChunk { Content = new TextContent { Text = "B" } },
                cancellationToken);
            await _events.SendSessionUpdateAsync(
                new AgentMessageChunk { Content = new TextContent { Text = "C" } },
                cancellationToken);

            // Thought chunks that should coalesce into a single ChatThought.
            await _events.SendSessionUpdateAsync(
                new AgentThoughtChunk { Content = new TextContent { Text = "X" } },
                cancellationToken);
            await _events.SendSessionUpdateAsync(
                new AgentThoughtChunk { Content = new TextContent { Text = "Y" } },
                cancellationToken);

            // End with a normal message chunk.
            await _events.SendSessionUpdateAsync(
                new AgentMessageChunk { Content = new TextContent { Text = "done" } },
                cancellationToken);

            return new PromptResponse { StopReason = StopReason.EndTurn };
        }

        public Task<SetSessionConfigOptionResponse>? SetSessionConfigOptionAsync(SetSessionConfigOptionRequest request, CancellationToken cancellationToken)
            => null;
    }
}
