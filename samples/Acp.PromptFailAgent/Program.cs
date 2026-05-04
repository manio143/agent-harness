using System.Threading;
using Agent.Acp.Acp;
using Agent.Acp.Schema;
using Agent.Acp.Transport;

// ACP sample agent that deterministically fails `session/prompt`.

var server = new AcpAgentServer(new PromptFailFactory());

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

internal sealed class PromptFailFactory : IAcpAgentFactory
{
    public Task<InitializeResponse> InitializeAsync(InitializeRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new InitializeResponse
        {
            ProtocolVersion = request.ProtocolVersion,
            AgentInfo = new AgentInfo
            {
                AdditionalProperties = new Dictionary<string, object>
                {
                    ["name"] = "acp-prompt-fail-agent",
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
        => new PromptFailSessionAgent();

    private sealed class PromptFailSessionAgent : IAcpSessionAgent
    {
        public Task<PromptResponse> PromptAsync(PromptRequest request, IAcpPromptTurn turn, CancellationToken cancellationToken)
            => throw new InvalidOperationException("boom: prompt failed");

        public Task<SetSessionConfigOptionResponse>? SetSessionConfigOptionAsync(SetSessionConfigOptionRequest request, CancellationToken cancellationToken)
            => null;
    }
}
