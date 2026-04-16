using Agent.Acp.Acp;
using Agent.Acp.Schema;

namespace Agent.Acp.Tests;

public class AcpInitializeCapabilityNegotiationTests
{
    [Fact]
    public async Task Initialize_ClientCapabilities_Are_Available_To_Session_Agent_Via_ClientCaller()
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();

        var factory = new CapturingFactory();
        var server = new AcpAgentServer(factory);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverTask = Task.Run(() => server.RunAsync(serverTransport, cts.Token), cts.Token);

        await using var client = new AcpClientConnection(clientTransport);

        _ = await client.RequestAsync<InitializeRequest, InitializeResponse>(
            "initialize",
            new InitializeRequest
            {
                ProtocolVersion = 1,
                ClientInfo = new ClientInfo { AdditionalProperties = new Dictionary<string, object> { ["name"] = "test", ["version"] = "0" } },
                ClientCapabilities = new ClientCapabilities
                {
                    Fs = new FileSystemCapabilities { ReadTextFile = true, WriteTextFile = true },
                    Terminal = true,
                },
            },
            cts.Token);

        var ses = await client.RequestAsync<NewSessionRequest, NewSessionResponse>(
            "session/new",
            new NewSessionRequest { Cwd = "/tmp", McpServers = new List<McpServer>() },
            cts.Token);

        var resp = await client.RequestAsync<PromptRequest, PromptResponse>(
            "session/prompt",
            new PromptRequest { SessionId = ses.SessionId, Prompt = new List<ContentBlock>() },
            cts.Token);

        Assert.Equal(StopReason.EndTurn, resp.StopReason.Value);

        // Assert the session agent observed the negotiated client capabilities.
        Assert.NotNull(factory.SeenClientCapabilities);
        Assert.NotNull(factory.SeenClientCapabilities!.Fs);
        Assert.True(factory.SeenClientCapabilities.Fs.ReadTextFile);
        Assert.True(factory.SeenClientCapabilities.Fs.WriteTextFile);
        Assert.True(factory.SeenClientCapabilities.Terminal);

        cts.Cancel();
        try { await serverTask; } catch { }
    }

    [Fact]
    public async Task Initialize_WhenClientCapabilitiesMissing_Server_Uses_Empty_Capabilities_Object()
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();

        var factory = new CapturingFactory();
        var server = new AcpAgentServer(factory);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverTask = Task.Run(() => server.RunAsync(serverTransport, cts.Token), cts.Token);

        await using var client = new AcpClientConnection(clientTransport);

        // ClientCapabilities intentionally omitted (null).
        _ = await client.RequestAsync<InitializeRequest, InitializeResponse>(
            "initialize",
            new InitializeRequest
            {
                ProtocolVersion = 1,
                ClientInfo = new ClientInfo { AdditionalProperties = new Dictionary<string, object> { ["name"] = "test", ["version"] = "0" } },
                ClientCapabilities = null!,
            },
            cts.Token);

        var ses = await client.RequestAsync<NewSessionRequest, NewSessionResponse>(
            "session/new",
            new NewSessionRequest { Cwd = "/tmp", McpServers = new List<McpServer>() },
            cts.Token);

        var resp = await client.RequestAsync<PromptRequest, PromptResponse>(
            "session/prompt",
            new PromptRequest { SessionId = ses.SessionId, Prompt = new List<ContentBlock>() },
            cts.Token);

        Assert.Equal(StopReason.EndTurn, resp.StopReason.Value);

        Assert.NotNull(factory.SeenClientCapabilities);
        // Empty caps: Fs may be null, but container should exist.
        Assert.False(factory.SeenClientCapabilities!.Terminal);

        cts.Cancel();
        try { await serverTask; } catch { }
    }

    private sealed class CapturingFactory : IAcpAgentFactory
    {
        public ClientCapabilities? SeenClientCapabilities { get; private set; }

        public Task<InitializeResponse> InitializeAsync(InitializeRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new InitializeResponse
            {
                ProtocolVersion = request.ProtocolVersion,
                AgentInfo = new AgentInfo { AdditionalProperties = new Dictionary<string, object> { ["name"] = "agent", ["version"] = "0" } },
                AgentCapabilities = new AgentCapabilities { PromptCapabilities = new PromptCapabilities() },
                AuthMethods = new List<AuthMethod>(),
            });

        public Task<NewSessionResponse> NewSessionAsync(NewSessionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new NewSessionResponse { SessionId = "ses_test", Modes = null });

        public IAcpSessionAgent CreateSessionAgent(string sessionId, IAcpClientCaller client, IAcpSessionEvents events)
        {
            SeenClientCapabilities = client.ClientCapabilities;
            return new NoopAgent();
        }

        private sealed class NoopAgent : IAcpSessionAgent
        {
            public Task<PromptResponse> PromptAsync(PromptRequest request, IAcpPromptTurn turn, CancellationToken cancellationToken) =>
                Task.FromResult(new PromptResponse { StopReason = StopReason.EndTurn });
        }
    }
}
