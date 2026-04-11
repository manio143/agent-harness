using Agent.Acp.Acp;
using Agent.Acp.Schema;

namespace Agent.Acp.Tests;

public class AcpOptionalMethodsTests
{
    [Fact]
    public async Task SessionLoad_WhenNotImplemented_Returns_MethodNotFound()
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();

        var server = new AcpAgentServer(new MinimalFactory());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverTask = Task.Run(() => server.RunAsync(serverTransport, cts.Token), cts.Token);

        await using var client = new AcpClientConnection(clientTransport);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.RequestAsync<LoadSessionRequest, LoadSessionResponse>(
            "session/load",
            new LoadSessionRequest { Cwd = "/tmp", McpServers = new List<McpServer>(), SessionId = "ses_1" },
            cts.Token));

        Assert.Contains("-32601", ex.Message);

        cts.Cancel();
        try { await serverTask; } catch { }
    }

    [Fact]
    public async Task SessionSetMode_WhenNotImplemented_Returns_MethodNotFound()
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();

        var server = new AcpAgentServer(new MinimalFactory());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverTask = Task.Run(() => server.RunAsync(serverTransport, cts.Token), cts.Token);

        await using var client = new AcpClientConnection(clientTransport);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.RequestAsync<SetSessionModeRequest, SetSessionModeResponse>(
            "session/set_mode",
            new SetSessionModeRequest { SessionId = "ses_1", ModeId = "mode_1" },
            cts.Token));

        Assert.Contains("-32601", ex.Message);

        cts.Cancel();
        try { await serverTask; } catch { }
    }

    [Fact]
    public async Task SessionLoad_And_SetMode_Work_WhenImplemented()
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();

        var server = new AcpAgentServer(new FactoryWithOptionals());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverTask = Task.Run(() => server.RunAsync(serverTransport, cts.Token), cts.Token);

        await using var client = new AcpClientConnection(clientTransport);

        var load = await client.RequestAsync<LoadSessionRequest, LoadSessionResponse>(
            "session/load",
            new LoadSessionRequest { Cwd = "/tmp", McpServers = new List<McpServer>(), SessionId = "ses_123" },
            cts.Token);

        Assert.NotNull(load.Modes);

        var setMode = await client.RequestAsync<SetSessionModeRequest, SetSessionModeResponse>(
            "session/set_mode",
            new SetSessionModeRequest { SessionId = "ses_123", ModeId = "mode_1" },
            cts.Token);

        Assert.NotNull(setMode);

        cts.Cancel();
        try { await serverTask; } catch { }
    }

    private sealed class MinimalFactory : IAcpAgentFactory
    {
        public Task<InitializeResponse> InitializeAsync(InitializeRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new InitializeResponse { ProtocolVersion = 1, AgentInfo = new AgentInfo(), AgentCapabilities = new AgentCapabilities(), AuthMethods = new List<AuthMethod>() });

        public Task<NewSessionResponse> NewSessionAsync(NewSessionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new NewSessionResponse { SessionId = "ses_test", Modes = new Modes2(), ConfigOptions = null });

        public IAcpSessionAgent CreateSessionAgent(string sessionId, IAcpClientCaller client, IAcpSessionEvents events) =>
            new MinimalSessionAgent();

        private sealed class MinimalSessionAgent : IAcpSessionAgent
        {
            public Task<PromptResponse> PromptAsync(PromptRequest request, CancellationToken cancellationToken) =>
                Task.FromResult(new PromptResponse());
        }
    }

    private sealed class FactoryWithOptionals : IAcpAgentFactory
    {
        public Task<InitializeResponse> InitializeAsync(InitializeRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new InitializeResponse { ProtocolVersion = 1, AgentInfo = new AgentInfo(), AgentCapabilities = new AgentCapabilities(), AuthMethods = new List<AuthMethod>() });

        public Task<NewSessionResponse> NewSessionAsync(NewSessionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new NewSessionResponse { SessionId = "ses_test", Modes = new Modes2(), ConfigOptions = null });

        public Task<LoadSessionResponse>? LoadSessionAsync(LoadSessionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new LoadSessionResponse { Modes = new Modes(), ConfigOptions = null });

        public IAcpSessionAgent CreateSessionAgent(string sessionId, IAcpClientCaller client, IAcpSessionEvents events) =>
            new SessionAgentWithSetMode();

        private sealed class SessionAgentWithSetMode : IAcpSessionAgent
        {
            public Task<PromptResponse> PromptAsync(PromptRequest request, CancellationToken cancellationToken) =>
                Task.FromResult(new PromptResponse());

            Task<SetSessionModeResponse>? IAcpSessionAgent.SetSessionModeAsync(SetSessionModeRequest request, CancellationToken cancellationToken) =>
                Task.FromResult(new SetSessionModeResponse());
        }
    }
}
