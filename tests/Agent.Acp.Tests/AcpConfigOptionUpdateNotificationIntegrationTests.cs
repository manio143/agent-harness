using System.Text.Json;
using Agent.Acp.Acp;
using Agent.Acp.Protocol;
using Agent.Acp.Schema;

namespace Agent.Acp.Tests;

public class AcpConfigOptionUpdateNotificationIntegrationTests
{
    [Fact]
    public async Task Agent_Can_Send_ConfigOptionUpdate_SessionUpdate_With_Complete_State()
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();

        var server = new AcpAgentServer(new Factory());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverTask = Task.Run(() => server.RunAsync(serverTransport, cts.Token), cts.Token);

        await using var client = new AcpClientConnection(clientTransport);

        var notifications = new List<JsonRpcNotification>();
        client.NotificationReceived += n => notifications.Add(n);

        _ = await client.RequestAsync<InitializeRequest, InitializeResponse>(
            "initialize",
            new InitializeRequest
            {
                ProtocolVersion = 1,
                ClientInfo = new ClientInfo { AdditionalProperties = new Dictionary<string, object> { ["name"] = "test", ["version"] = "0" } },
                ClientCapabilities = new ClientCapabilities(),
            },
            cts.Token);

        var newSes = await client.RequestAsync<NewSessionRequest, NewSessionResponse>(
            "session/new",
            new NewSessionRequest { Cwd = "/tmp", McpServers = new List<McpServer>() },
            cts.Token);

        _ = await client.RequestAsync<PromptRequest, PromptResponse>(
            "session/prompt",
            new PromptRequest { SessionId = newSes.SessionId, Prompt = new List<ContentBlock>() },
            cts.Token);

        var updateParams = notifications
            .Where(n => n.Method == "session/update")
            .Select(n => n.Params)
            .Where(p => p.HasValue)
            .Select(p => p!.Value)
            .ToList();

        var configUpdate = updateParams.FirstOrDefault(p => GetUpdateKind(p) == "config_option_update");
        Assert.NotEqual(default, configUpdate);

        var configOptions = configUpdate.GetProperty("update").GetProperty("configOptions");
        Assert.Equal(JsonValueKind.Array, configOptions.ValueKind);
        Assert.True(configOptions.GetArrayLength() > 0);

        cts.Cancel();
        try { await serverTask; } catch { }
    }

    private static string? GetUpdateKind(JsonElement @params)
    {
        if (!@params.TryGetProperty("update", out var u)) return null;
        if (!u.TryGetProperty("sessionUpdate", out var k)) return null;
        return k.GetString();
    }

    private sealed class Factory : IAcpAgentFactory
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
            new Agent(sessionId, events);

        private sealed class Agent : IAcpSessionAgent
        {
            private readonly string _sessionId;
            private readonly IAcpSessionEvents _events;

            public Agent(string sessionId, IAcpSessionEvents events)
            {
                _sessionId = sessionId;
                _events = events;
            }

            public async Task<PromptResponse> PromptAsync(PromptRequest request, IAcpPromptTurn turn, CancellationToken cancellationToken)
            {
                var config = new List<SessionConfigOption>
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
                };

                await _events.SendSessionUpdateAsync(new
                {
                    sessionUpdate = "config_option_update",
                    configOptions = config,
                }, cancellationToken);

                return new PromptResponse { StopReason = StopReason.EndTurn };
            }
        }
    }
}
