using Agent.Acp.Acp;
using Agent.Acp.Schema;

namespace Agent.Acp.Tests;

public class AcpInitializeContractTests
{
    [Fact]
    public async Task Initialize_Returns_AgentInfo_And_Capabilities()
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();

        var agent = new MinimalInitFactory();
        var server = new AcpAgentServer(agent);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverTask = Task.Run(() => server.RunAsync(serverTransport, cts.Token), cts.Token);

        await using var client = new AcpClientConnection(clientTransport);

        var resp = await client.RequestAsync<InitializeRequest, InitializeResponse>(
            "initialize",
            new InitializeRequest
            {
                ProtocolVersion = 1,
                ClientInfo = new ClientInfo
                {
                    AdditionalProperties = new Dictionary<string, object>
                    {
                        ["name"] = "test-client",
                        ["version"] = "0.0",
                    }
                },
                ClientCapabilities = new ClientCapabilities
                {
                    Fs = new FileSystemCapabilities { ReadTextFile = true, WriteTextFile = true },
                    Terminal = false,
                },
            },
            cts.Token);

        Assert.Equal(1, resp.ProtocolVersion);
        Assert.NotNull(resp.AgentInfo);
        Assert.NotNull(resp.AgentCapabilities);
        Assert.NotNull(resp.AuthMethods);

        // We don't assume strongly typed AgentInfo fields (generator currently uses extension data).
        Assert.True(resp.AgentInfo.AdditionalProperties.TryGetValue("name", out var name));
        Assert.Equal("agent", name?.ToString());

        cts.Cancel();
        try { await serverTask; } catch { }
    }

    private sealed class MinimalInitFactory : IAcpAgentFactory
    {
        public Task<InitializeResponse> InitializeAsync(InitializeRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new InitializeResponse
            {
                ProtocolVersion = request.ProtocolVersion,
                AgentInfo = new AgentInfo
                {
                    AdditionalProperties = new Dictionary<string, object>
                    {
                        ["name"] = "agent",
                        ["version"] = "0.0",
                    }
                },
                AgentCapabilities = new AgentCapabilities(),
                AuthMethods = new List<AuthMethod>(),
            });

        public Task<NewSessionResponse> NewSessionAsync(NewSessionRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new NewSessionResponse { SessionId = "ses_test", Modes = new Modes2() });

        public IAcpSessionAgent CreateSessionAgent(string sessionId, IAcpClientCaller client, IAcpSessionEvents events) =>
            new NoopSessionAgent();

        private sealed class NoopSessionAgent : IAcpSessionAgent
        {
            public Task<PromptResponse> PromptAsync(PromptRequest request, IAcpPromptTurn turn, CancellationToken cancellationToken)
                => Task.FromResult(new PromptResponse());
        }
    }
}
