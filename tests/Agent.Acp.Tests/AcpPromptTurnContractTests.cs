using Agent.Acp.Acp;
using Agent.Acp.Schema;

namespace Agent.Acp.Tests;

public class AcpPromptTurnContractTests
{
    [Fact]
    public async Task PromptResponse_Must_Include_StopReason()
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();

        var server = new AcpAgentServer(new FactoryReturningMissingStopReason());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverTask = Task.Run(() => server.RunAsync(serverTransport, cts.Token), cts.Token);

        await using var client = new AcpClientConnection(clientTransport);

        _ = await client.RequestAsync<InitializeRequest, InitializeResponse>(
            "initialize",
            new InitializeRequest { ProtocolVersion = 1, ClientInfo = new ClientInfo { AdditionalProperties = new Dictionary<string, object> { ["name"] = "test", ["version"] = "0" } }, ClientCapabilities = new ClientCapabilities() },
            cts.Token);

        var newSes = await client.RequestAsync<NewSessionRequest, NewSessionResponse>(
            "session/new",
            new NewSessionRequest { Cwd = "/tmp", McpServers = new List<McpServer>() },
            cts.Token);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.RequestAsync<PromptRequest, PromptResponse>(
            "session/prompt",
            new PromptRequest { SessionId = newSes.SessionId, Prompt = new List<ContentBlock>() },
            cts.Token));

        Assert.Contains("stopReason", ex.Message, StringComparison.OrdinalIgnoreCase);

        cts.Cancel();
        try { await serverTask; } catch { }
    }

    private sealed class FactoryReturningMissingStopReason : IAcpAgentFactory
    {
        public Task<InitializeResponse> InitializeAsync(InitializeRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new InitializeResponse
            {
                ProtocolVersion = request.ProtocolVersion,
                AgentInfo = new AgentInfo { AdditionalProperties = new Dictionary<string, object> { ["name"] = "agent", ["version"] = "0" } },
                AgentCapabilities = new AgentCapabilities { PromptCapabilities = new PromptCapabilities() },
                AuthMethods = new List<AuthMethod>(),
            });

        public Task<NewSessionResponse> NewSessionAsync(NewSessionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new NewSessionResponse { SessionId = "ses_test", Modes = new Modes2() });

        public IAcpSessionAgent CreateSessionAgent(string sessionId, IAcpClientCaller client, IAcpSessionEvents events) =>
            new SessionAgentReturningMissingStopReason();

        private sealed class SessionAgentReturningMissingStopReason : IAcpSessionAgent
        {
            public Task<PromptResponse> PromptAsync(PromptRequest request, IAcpPromptTurn turn, CancellationToken cancellationToken) =>
                // Intentionally violate spec.
                Task.FromResult(new PromptResponse());
        }
    }
}
