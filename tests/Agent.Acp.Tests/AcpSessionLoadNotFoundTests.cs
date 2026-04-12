using Agent.Acp.Acp;
using Agent.Acp.Schema;

namespace Agent.Acp.Tests;

public sealed class AcpSessionLoadNotFoundTests
{
    [Fact]
    public async Task session_load_unknown_sessionId_returns_invalid_params()
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();

        var factory = new NotFoundFactory();
        var server = new AcpAgentServer(factory);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverTask = Task.Run(() => server.RunAsync(serverTransport, cts.Token), cts.Token);

        await using var client = new AcpClientConnection(clientTransport);

        await client.RequestAsync<InitializeRequest, InitializeResponse>(
            "initialize",
            new InitializeRequest
            {
                ProtocolVersion = 1,
                ClientInfo = new ClientInfo { AdditionalProperties = new Dictionary<string, object> { ["name"] = "test", ["version"] = "0" } },
                ClientCapabilities = new ClientCapabilities(),
            },
            cts.Token);

        var act = async () => await client.RequestAsync<LoadSessionRequest, LoadSessionResponse>(
            "session/load",
            new LoadSessionRequest
            {
                SessionId = "missing",
                Cwd = "/tmp",
                McpServers = new List<McpServer>(),
            },
            cts.Token);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(act);
        Assert.Contains("JSON-RPC error -32602", ex.Message);

        cts.Cancel();
        try { await serverTask; } catch { }
    }

    private sealed class NotFoundFactory : IAcpAgentFactory
    {
        public Task<InitializeResponse> InitializeAsync(InitializeRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new InitializeResponse
            {
                ProtocolVersion = request.ProtocolVersion,
                AgentInfo = new AgentInfo { AdditionalProperties = new Dictionary<string, object> { ["name"] = "agent", ["version"] = "0" } },
                AgentCapabilities = new AgentCapabilities
                {
                    LoadSession = true,
                    McpCapabilities = new McpCapabilities(),
                    PromptCapabilities = new PromptCapabilities(),
                    SessionCapabilities = new SessionCapabilities { List = new Agent.Acp.Schema.List() },
                },
                AuthMethods = new List<AuthMethod>(),
            });

        public Task<NewSessionResponse> NewSessionAsync(NewSessionRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new NewSessionResponse { SessionId = "s", ConfigOptions = new List<SessionConfigOption>(), Modes = null });

        public Task<LoadSessionResponse>? LoadSessionAsync(LoadSessionRequest request, CancellationToken cancellationToken)
            => throw new AcpJsonRpcException(-32602, "Session not found");

        public IAcpSessionAgent CreateSessionAgent(string sessionId, IAcpClientCaller client, IAcpSessionEvents events)
            => throw new NotSupportedException();
    }
}
