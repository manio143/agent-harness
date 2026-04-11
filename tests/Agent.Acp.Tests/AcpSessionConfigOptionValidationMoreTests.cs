using Agent.Acp.Acp;
using Agent.Acp.Schema;

namespace Agent.Acp.Tests;

public class AcpSessionConfigOptionValidationMoreTests
{
    [Fact]
    public async Task SessionLoad_Rejects_ConfigOptions_When_CurrentValue_Not_In_Options()
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();
        var server = new AcpAgentServer(new BadFactory(), supportedProtocolVersion: 1);

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

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.RequestAsync<LoadSessionRequest, LoadSessionResponse>(
            "session/load",
            new LoadSessionRequest { SessionId = "ses_test", Cwd = "/tmp", McpServers = new List<McpServer>() },
            cts.Token));

        Assert.Contains("JSON-RPC error -32602", ex.Message);
        Assert.Contains("currentValue", ex.Message);

        cts.Cancel();
        try { await serverTask; } catch { }
    }

    [Fact]
    public async Task SetConfigOption_Response_Must_Have_Valid_CurrentValue()
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();
        var server = new AcpAgentServer(new BadSetFactory(), supportedProtocolVersion: 1);

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

        var ses = await client.RequestAsync<NewSessionRequest, NewSessionResponse>(
            "session/new",
            new NewSessionRequest { Cwd = "/tmp", McpServers = new List<McpServer>() },
            cts.Token);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.RequestAsync<SetSessionConfigOptionRequest, SetSessionConfigOptionResponse>(
            "session/set_config_option",
            new SetSessionConfigOptionRequest { SessionId = ses.SessionId, ConfigId = "mode", Value = "code" },
            cts.Token));

        Assert.Contains("JSON-RPC error -32602", ex.Message);
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
                AgentCapabilities = new AgentCapabilities { PromptCapabilities = new PromptCapabilities(), LoadSession = true },
                AuthMethods = new List<AuthMethod>(),
            });

        public Task<NewSessionResponse> NewSessionAsync(NewSessionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new NewSessionResponse { SessionId = "ses_test", Modes = null });

        public Task<LoadSessionResponse>? LoadSessionAsync(LoadSessionRequest request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new LoadSessionResponse
            {
                Modes = null,
                ConfigOptions = new List<SessionConfigOption>
                {
                    new SessionConfigOption
                    {
                        Id = "mode",
                        Name = "Mode",
                        Type = SessionConfigOptionType.Select,
                        CurrentValue = "invalid",
                        Options = new SessionConfigSelectOptions
                        {
                            new SessionConfigSelectOption { Value = "ask", Name = "Ask" },
                            new SessionConfigSelectOption { Value = "code", Name = "Code" },
                        },
                    },
                },
            });
        }

        public IAcpSessionAgent CreateSessionAgent(string sessionId, IAcpClientCaller client, IAcpSessionEvents events) =>
            new Agent();

        private sealed class Agent : IAcpSessionAgent
        {
            public Task<PromptResponse> PromptAsync(PromptRequest request, IAcpPromptTurn turn, CancellationToken cancellationToken) =>
                Task.FromResult(new PromptResponse { StopReason = StopReason.EndTurn });
        }
    }

    private sealed class BadSetFactory : IAcpAgentFactory
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
            Task.FromResult(new NewSessionResponse { SessionId = "ses_test", Modes = null });

        public IAcpSessionAgent CreateSessionAgent(string sessionId, IAcpClientCaller client, IAcpSessionEvents events) =>
            new Agent();

        private sealed class Agent : IAcpSessionAgent
        {
            public Task<PromptResponse> PromptAsync(PromptRequest request, IAcpPromptTurn turn, CancellationToken cancellationToken) =>
                Task.FromResult(new PromptResponse { StopReason = StopReason.EndTurn });

            public Task<SetSessionConfigOptionResponse>? SetSessionConfigOptionAsync(SetSessionConfigOptionRequest request, CancellationToken cancellationToken)
            {
                return Task.FromResult(new SetSessionConfigOptionResponse
                {
                    ConfigOptions = new List<SessionConfigOption>
                    {
                        new SessionConfigOption
                        {
                            Id = "mode",
                            Name = "Mode",
                            Type = SessionConfigOptionType.Select,
                            CurrentValue = "invalid",
                            Options = new SessionConfigSelectOptions
                            {
                                new SessionConfigSelectOption { Value = "ask", Name = "Ask" },
                                new SessionConfigSelectOption { Value = "code", Name = "Code" },
                            },
                        },
                    },
                });
            }
        }
    }
}
