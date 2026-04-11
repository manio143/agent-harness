using Agent.Acp.Acp;
using Agent.Acp.Schema;

namespace Agent.Acp.Tests;

public class AcpSessionListTests
{
    [Fact]
    public async Task SessionList_Returns_InvalidParams_When_Capability_Not_Advertised()
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();
        var server = new AcpAgentServer(new Factory(advertise: false, implement: true));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverTask = Task.Run(() => server.RunAsync(serverTransport, cts.Token), cts.Token);

        await using var client = new AcpClientConnection(clientTransport);

        _ = await client.RequestAsync<InitializeRequest, InitializeResponse>(
            "initialize",
            new InitializeRequest
            {
                ProtocolVersion = 1,
                ClientInfo = new ClientInfo { AdditionalProperties = new Dictionary<string, object> { ["name"] = "test", ["version"] = "0" } },
                ClientCapabilities = new ClientCapabilities(),
            },
            cts.Token);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.RequestAsync<ListSessionsRequest, ListSessionsResponse>(
            "session/list",
            new ListSessionsRequest(),
            cts.Token));

        Assert.Contains("JSON-RPC error -32602", ex.Message);
        Assert.Contains("sessionCapabilities.list", ex.Message);

        cts.Cancel();
        try { await serverTask; } catch { }
    }

    [Fact]
    public async Task SessionList_Returns_MethodNotFound_When_Not_Implemented()
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();
        var server = new AcpAgentServer(new Factory(advertise: true, implement: false));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverTask = Task.Run(() => server.RunAsync(serverTransport, cts.Token), cts.Token);

        await using var client = new AcpClientConnection(clientTransport);

        _ = await client.RequestAsync<InitializeRequest, InitializeResponse>(
            "initialize",
            new InitializeRequest
            {
                ProtocolVersion = 1,
                ClientInfo = new ClientInfo { AdditionalProperties = new Dictionary<string, object> { ["name"] = "test", ["version"] = "0" } },
                ClientCapabilities = new ClientCapabilities(),
            },
            cts.Token);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.RequestAsync<ListSessionsRequest, ListSessionsResponse>(
            "session/list",
            new ListSessionsRequest(),
            cts.Token));

        Assert.Contains("JSON-RPC error -32601", ex.Message);
        Assert.Contains("session/list not supported", ex.Message);

        cts.Cancel();
        try { await serverTask; } catch { }
    }

    [Fact]
    public async Task SessionList_Rejects_NonAbsolute_Cwd_Filter()
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();
        var server = new AcpAgentServer(new Factory(advertise: true, implement: true));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverTask = Task.Run(() => server.RunAsync(serverTransport, cts.Token), cts.Token);

        await using var client = new AcpClientConnection(clientTransport);

        _ = await client.RequestAsync<InitializeRequest, InitializeResponse>(
            "initialize",
            new InitializeRequest
            {
                ProtocolVersion = 1,
                ClientInfo = new ClientInfo { AdditionalProperties = new Dictionary<string, object> { ["name"] = "test", ["version"] = "0" } },
                ClientCapabilities = new ClientCapabilities(),
            },
            cts.Token);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.RequestAsync<ListSessionsRequest, ListSessionsResponse>(
            "session/list",
            new ListSessionsRequest { Cwd = "relative" },
            cts.Token));

        Assert.Contains("JSON-RPC error -32602", ex.Message);
        Assert.Contains("cwd must be an absolute path", ex.Message);

        cts.Cancel();
        try { await serverTask; } catch { }
    }

    [Fact]
    public async Task SessionList_Returns_Sessions_Array_Can_Be_Empty()
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();
        var server = new AcpAgentServer(new Factory(advertise: true, implement: true, returnEmpty: true));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverTask = Task.Run(() => server.RunAsync(serverTransport, cts.Token), cts.Token);

        await using var client = new AcpClientConnection(clientTransport);

        _ = await client.RequestAsync<InitializeRequest, InitializeResponse>(
            "initialize",
            new InitializeRequest
            {
                ProtocolVersion = 1,
                ClientInfo = new ClientInfo { AdditionalProperties = new Dictionary<string, object> { ["name"] = "test", ["version"] = "0" } },
                ClientCapabilities = new ClientCapabilities(),
            },
            cts.Token);

        var resp = await client.RequestAsync<ListSessionsRequest, ListSessionsResponse>(
            "session/list",
            new ListSessionsRequest(),
            cts.Token);

        Assert.NotNull(resp.Sessions);
        Assert.Empty(resp.Sessions);

        cts.Cancel();
        try { await serverTask; } catch { }
    }

    private sealed class Factory : IAcpAgentFactory
    {
        private readonly bool _advertise;
        private readonly bool _implement;
        private readonly bool _returnEmpty;

        public Factory(bool advertise, bool implement, bool returnEmpty = false)
        {
            _advertise = advertise;
            _implement = implement;
            _returnEmpty = returnEmpty;
        }

        public Task<InitializeResponse> InitializeAsync(InitializeRequest request, CancellationToken cancellationToken)
        {
            var caps = new AgentCapabilities { PromptCapabilities = new PromptCapabilities() };
            if (_advertise)
            {
                caps.SessionCapabilities = new SessionCapabilities { List = new List() };
            }

            return Task.FromResult(new InitializeResponse
            {
                ProtocolVersion = request.ProtocolVersion,
                AgentInfo = new AgentInfo { AdditionalProperties = new Dictionary<string, object> { ["name"] = "agent", ["version"] = "0" } },
                AgentCapabilities = caps,
                AuthMethods = new List<AuthMethod>(),
            });
        }

        public Task<NewSessionResponse> NewSessionAsync(NewSessionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new NewSessionResponse { SessionId = "ses_test", Modes = null });

        public Task<ListSessionsResponse>? ListSessionsAsync(ListSessionsRequest request, CancellationToken cancellationToken)
        {
            if (!_implement)
                return null;

            if (_returnEmpty)
                return Task.FromResult(new ListSessionsResponse { Sessions = new List<SessionInfo>() });

            return Task.FromResult(new ListSessionsResponse
            {
                Sessions = new List<SessionInfo>
                {
                    new SessionInfo { SessionId = "ses_1", Cwd = "/tmp", Title = "t" },
                },
            });
        }

        public Task<LoadSessionResponse>? LoadSessionAsync(LoadSessionRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new LoadSessionResponse { Modes = null, ConfigOptions = null });

        public IAcpSessionAgent CreateSessionAgent(string sessionId, IAcpClientCaller client, IAcpSessionEvents events) =>
            new Agent();

        private sealed class Agent : IAcpSessionAgent
        {
            public Task<PromptResponse> PromptAsync(PromptRequest request, IAcpPromptTurn turn, CancellationToken cancellationToken) =>
                Task.FromResult(new PromptResponse { StopReason = StopReason.EndTurn });
        }
    }
}
