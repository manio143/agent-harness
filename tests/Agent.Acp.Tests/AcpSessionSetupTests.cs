using Agent.Acp.Acp;
using Agent.Acp.Schema;

namespace Agent.Acp.Tests;

public class AcpSessionSetupTests
{
    [Fact]
    public async Task SessionNew_Rejects_RelativeCwd()
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();

        var server = new AcpAgentServer(new Factory(loadSession: false));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverTask = Task.Run(() => server.RunAsync(serverTransport, cts.Token), cts.Token);

        await using var client = new AcpClientConnection(clientTransport);

        _ = await client.RequestAsync<InitializeRequest, InitializeResponse>(
            "initialize",
            new InitializeRequest { ProtocolVersion = 1, ClientInfo = new ClientInfo { AdditionalProperties = new Dictionary<string, object> { ["name"] = "test", ["version"] = "0" } }, ClientCapabilities = new ClientCapabilities() },
            cts.Token);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.RequestAsync<NewSessionRequest, NewSessionResponse>(
            "session/new",
            new NewSessionRequest { Cwd = "relative", McpServers = new List<McpServer>() },
            cts.Token));

        Assert.Contains("cwd must be an absolute path", ex.Message, StringComparison.OrdinalIgnoreCase);

        cts.Cancel();
        try { await serverTask; } catch { }
    }

    [Fact]
    public async Task SessionLoad_Is_Rejected_When_AgentDidNotAdvertise_LoadSessionCapability()
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();

        var server = new AcpAgentServer(new Factory(loadSession: false));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverTask = Task.Run(() => server.RunAsync(serverTransport, cts.Token), cts.Token);

        await using var client = new AcpClientConnection(clientTransport);

        _ = await client.RequestAsync<InitializeRequest, InitializeResponse>(
            "initialize",
            new InitializeRequest { ProtocolVersion = 1, ClientInfo = new ClientInfo { AdditionalProperties = new Dictionary<string, object> { ["name"] = "test", ["version"] = "0" } }, ClientCapabilities = new ClientCapabilities() },
            cts.Token);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.RequestAsync<LoadSessionRequest, LoadSessionResponse>(
            "session/load",
            new LoadSessionRequest { SessionId = "ses_1", Cwd = "/tmp", McpServers = new List<McpServer>() },
            cts.Token));

        Assert.Contains("loadSession", ex.Message, StringComparison.OrdinalIgnoreCase);

        cts.Cancel();
        try { await serverTask; } catch { }
    }

    private sealed class Factory : IAcpAgentFactory
    {
        private readonly bool _loadSession;

        public Factory(bool loadSession)
        {
            _loadSession = loadSession;
        }

        public Task<InitializeResponse> InitializeAsync(InitializeRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new InitializeResponse
            {
                ProtocolVersion = request.ProtocolVersion,
                AgentInfo = new AgentInfo { AdditionalProperties = new Dictionary<string, object> { ["name"] = "agent", ["version"] = "0" } },
                AgentCapabilities = new AgentCapabilities
                {
                    LoadSession = _loadSession,
                    PromptCapabilities = new PromptCapabilities(),
                },
                AuthMethods = new List<AuthMethod>(),
            });

        public Task<NewSessionResponse> NewSessionAsync(NewSessionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new NewSessionResponse { SessionId = "ses_test", Modes = new Modes2() });

        public Task<LoadSessionResponse>? LoadSessionAsync(LoadSessionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new LoadSessionResponse { Modes = new Modes(), ConfigOptions = null });

        public IAcpSessionAgent CreateSessionAgent(string sessionId, IAcpClientCaller client, IAcpSessionEvents events) =>
            new NoopSessionAgent();

        private sealed class NoopSessionAgent : IAcpSessionAgent
        {
            public Task<PromptResponse> PromptAsync(PromptRequest request, IAcpPromptTurn turn, CancellationToken cancellationToken) =>
                Task.FromResult(new PromptResponse());
        }
    }
}
