using System.Text.Json;
using Agent.Acp.Acp;
using Agent.Acp.Protocol;
using Agent.Acp.Schema;

namespace Agent.Acp.Tests;

public class AcpPermissionRequestIntegrationTests
{
    [Fact]
    public async Task Agent_Can_Request_Permission_From_Client_During_Prompt()
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();

        var server = new AcpAgentServer(new Factory());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverTask = Task.Run(() => server.RunAsync(serverTransport, cts.Token), cts.Token);

        await using var client = new AcpClientConnection(clientTransport);

        client.RequestHandler = (req, _) =>
        {
            Assert.Equal("session/request_permission", req.Method);

            // Minimal decode: respond with selected option.
            var result = new RequestPermissionResponse
            {
                Outcome = new RequestPermissionOutcome
                {
                    Outcome = RequestPermissionOutcome.Selected,
                    OptionId = "allow-once",
                },
            };

            var json = JsonSerializer.Serialize(result, AcpJson.Options);
            using var doc = JsonDocument.Parse(json);
            return Task.FromResult(doc.RootElement.Clone());
        };

        _ = await client.RequestAsync<InitializeRequest, InitializeResponse>(
            "initialize",
            new InitializeRequest
            {
                ProtocolVersion = 1,
                ClientInfo = new ClientInfo { AdditionalProperties = new Dictionary<string, object> { ["name"] = "test", ["version"] = "0" } },
                ClientCapabilities = new ClientCapabilities { Fs = new FileSystemCapabilities { ReadTextFile = true, WriteTextFile = true }, Terminal = true },
            },
            cts.Token);

        var newSes = await client.RequestAsync<NewSessionRequest, NewSessionResponse>(
            "session/new",
            new NewSessionRequest { Cwd = "/tmp", McpServers = new List<McpServer>() },
            cts.Token);

        var resp = await client.RequestAsync<PromptRequest, PromptResponse>(
            "session/prompt",
            new PromptRequest { SessionId = newSes.SessionId, Prompt = new List<ContentBlock>() },
            cts.Token);

        Assert.Equal(StopReason.EndTurn, resp.StopReason.Value);

        cts.Cancel();
        try { await serverTask; } catch { }
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
            new Agent(client);

        private sealed class Agent : IAcpSessionAgent
        {
            private readonly IAcpClientCaller _client;

            public Agent(IAcpClientCaller client)
            {
                _client = client;
            }

            public async Task<PromptResponse> PromptAsync(PromptRequest request, IAcpPromptTurn turn, CancellationToken cancellationToken)
            {
                var toolCallUpdate = new ToolCallUpdate { ToolCallId = "call_1" };
                var options = new List<PermissionOption>
                {
                    new PermissionOption { OptionId = "allow-once", Name = "Allow once", Kind = PermissionOptionKind.AllowOnce },
                    new PermissionOption { OptionId = "reject-once", Name = "Reject", Kind = PermissionOptionKind.RejectOnce },
                };

                var outcome = await _client.RequestPermissionAsync(new RequestPermissionRequest
                {
                    SessionId = request.SessionId,
                    ToolCall = toolCallUpdate,
                    Options = options,
                }, cancellationToken);

                if (outcome.Outcome.Outcome != RequestPermissionOutcome.Selected)
                    throw new InvalidOperationException("Expected selected");

                return new PromptResponse { StopReason = StopReason.EndTurn };
            }
        }
    }
}
