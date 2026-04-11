using Agent.Acp.Acp;
using Agent.Acp.Schema;

namespace Agent.Acp.Tests;

public class AcpSessionConfigOptionTests
{
    [Fact]
    public async Task SessionSetConfigOption_Returns_MethodNotFound_When_Not_Supported()
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();
        var server = new AcpAgentServer(new Factory(support: false));

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

        Assert.Contains("JSON-RPC error -32601", ex.Message);
        Assert.Contains("session/set_config_option not supported", ex.Message);

        cts.Cancel();
        try { await serverTask; } catch { }
    }

    [Fact]
    public async Task SessionSetConfigOption_Response_Must_Include_ConfigOptions()
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();
        var server = new AcpAgentServer(new Factory(support: true, returnEmpty: true));

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
        Assert.Contains("SetSessionConfigOptionResponse.configOptions is required", ex.Message);

        cts.Cancel();
        try { await serverTask; } catch { }
    }

    private sealed class Factory : IAcpAgentFactory
    {
        private readonly bool _support;
        private readonly bool _returnEmpty;

        public Factory(bool support, bool returnEmpty = false)
        {
            _support = support;
            _returnEmpty = returnEmpty;
        }

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
                        CurrentValue = "ask",
                        Options = new SessionConfigSelectOptions
                        {
                            new SessionConfigSelectOption { Value = "ask", Name = "Ask" },
                            new SessionConfigSelectOption { Value = "code", Name = "Code" },
                        },
                    },
                },
            });

        public IAcpSessionAgent CreateSessionAgent(string sessionId, IAcpClientCaller client, IAcpSessionEvents events) =>
            new Agent(_support, _returnEmpty);

        private sealed class Agent : IAcpSessionAgent
        {
            private readonly bool _support;
            private readonly bool _returnEmpty;

            public Agent(bool support, bool returnEmpty)
            {
                _support = support;
                _returnEmpty = returnEmpty;
            }

            public Task<PromptResponse> PromptAsync(PromptRequest request, IAcpPromptTurn turn, CancellationToken cancellationToken)
                => Task.FromResult(new PromptResponse { StopReason = StopReason.EndTurn });

            public Task<SetSessionConfigOptionResponse>? SetSessionConfigOptionAsync(SetSessionConfigOptionRequest request, CancellationToken cancellationToken)
            {
                if (!_support)
                    return null;

                if (_returnEmpty)
                    return Task.FromResult(new SetSessionConfigOptionResponse());

                return Task.FromResult(new SetSessionConfigOptionResponse
                {
                    ConfigOptions = new List<SessionConfigOption>
                    {
                        new SessionConfigOption
                        {
                            Id = request.ConfigId,
                            Name = "Mode",
                            Type = SessionConfigOptionType.Select,
                            CurrentValue = request.Value,
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
