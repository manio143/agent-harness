using Agent.Acp.Acp;
using Agent.Acp.Schema;

namespace Agent.Acp.Tests;

public class AcpInitializationTests
{
    [Fact]
    public async Task Initialize_WhenClientRequestsUnsupportedVersion_AgentRespondsWithSupportedVersion()
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();

        var factory = new InitFactory();
        var server = new AcpAgentServer(factory, supportedProtocolVersion: 1);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverTask = Task.Run(() => server.RunAsync(serverTransport, cts.Token), cts.Token);

        await using var client = new AcpClientConnection(clientTransport);

        var resp = await client.RequestAsync<InitializeRequest, InitializeResponse>(
            "initialize",
            new InitializeRequest
            {
                ProtocolVersion = 999,
                ClientInfo = new ClientInfo { AdditionalProperties = new Dictionary<string, object> { ["name"] = "test", ["version"] = "0" } },
                ClientCapabilities = new ClientCapabilities(),
            },
            cts.Token);

        Assert.Equal(1, resp.ProtocolVersion);

        cts.Cancel();
        try { await serverTask; } catch { }
    }

    private sealed class InitFactory : IAcpAgentFactory
    {
        public Task<InitializeResponse> InitializeAsync(InitializeRequest request, CancellationToken cancellationToken) =>
            // Deliberately "wrong" version to prove comm layer enforces negotiation.
            Task.FromResult(new InitializeResponse
            {
                ProtocolVersion = request.ProtocolVersion,
                AgentInfo = new AgentInfo { AdditionalProperties = new Dictionary<string, object> { ["name"] = "agent", ["version"] = "0" } },
                AgentCapabilities = new AgentCapabilities(),
                AuthMethods = new List<AuthMethod>(),
            });

        public Task<NewSessionResponse> NewSessionAsync(NewSessionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new NewSessionResponse { SessionId = "ses_test", Modes = new Modes2() });

        public IAcpSessionAgent CreateSessionAgent(string sessionId, IAcpClientCaller client, IAcpSessionEvents events) =>
            new NoopSessionAgent();

        private sealed class NoopSessionAgent : IAcpSessionAgent
        {
            public Task<PromptResponse> PromptAsync(PromptRequest request, IAcpPromptTurn turn, CancellationToken cancellationToken) =>
                Task.FromResult(new PromptResponse { StopReason = StopReason.EndTurn });
        }
    }
}
