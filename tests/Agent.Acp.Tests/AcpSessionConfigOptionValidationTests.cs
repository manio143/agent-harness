using Agent.Acp.Acp;
using Agent.Acp.Schema;

namespace Agent.Acp.Tests;

public class AcpSessionConfigOptionValidationTests
{
    [Fact]
    public async Task SessionNew_Rejects_ConfigOptions_When_CurrentValue_Not_In_Options()
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();
        var server = new AcpAgentServer(new BadFactory());

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

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.RequestAsync<NewSessionRequest, NewSessionResponse>(
            "session/new",
            new NewSessionRequest { Cwd = "/tmp", McpServers = new List<McpServer>() },
            cts.Token));

        Assert.Contains("JSON-RPC error -32602", ex.Message);
        Assert.Contains("configOptions", ex.Message);
        Assert.Contains("currentValue", ex.Message);

        cts.Cancel();
        try { await serverTask; } catch { }
    }

    private sealed class BadFactory : IAcpAgentFactory
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
            Task.FromResult(new NewSessionResponse
            {
                SessionId = "ses_test",
                Modes = new Modes2(),
                ConfigOptions = new List<SessionConfigOption>
                {
                    new SessionConfigOption
                    {
                        Id = "mode",
                        Name = "Mode",
                        Type = SessionConfigOptionType.Select,
                        CurrentValue = "not-a-valid-value",
                        Options = new SessionConfigSelectOptions
                        {
                            new SessionConfigSelectOption { Value = "ask", Name = "Ask" },
                            new SessionConfigSelectOption { Value = "code", Name = "Code" },
                        },
                    },
                },
            });

        public IAcpSessionAgent CreateSessionAgent(string sessionId, IAcpClientCaller client, IAcpSessionEvents events) =>
            new Agent();

        private sealed class Agent : IAcpSessionAgent
        {
            public Task<PromptResponse> PromptAsync(PromptRequest request, IAcpPromptTurn turn, CancellationToken cancellationToken) =>
                Task.FromResult(new PromptResponse { StopReason = StopReason.EndTurn });
        }
    }
}
