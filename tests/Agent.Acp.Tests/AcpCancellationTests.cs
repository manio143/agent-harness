using Agent.Acp.Acp;
using Agent.Acp.Schema;

namespace Agent.Acp.Tests;

public class AcpCancellationTests
{
    [Fact]
    public async Task SessionCancel_Stops_A_Running_Prompt()
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();

        var agent = new BlockingFactory();
        var server = new AcpAgentServer(agent);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverTask = Task.Run(() => server.RunAsync(serverTransport, cts.Token), cts.Token);

        await using var client = new AcpClientConnection(clientTransport);

        _ = await client.RequestAsync<InitializeRequest, InitializeResponse>(
            "initialize",
            new InitializeRequest
            {
                ProtocolVersion = 1,
                ClientInfo = new ClientInfo { AdditionalProperties = new Dictionary<string, object> { ["name"] = "test", ["version"] = "0" } },
                ClientCapabilities = new ClientCapabilities { Fs = new FileSystemCapabilities { ReadTextFile = false, WriteTextFile = false } },
            },
            cts.Token);

        var newSes = await client.RequestAsync<NewSessionRequest, NewSessionResponse>(
            "session/new",
            new NewSessionRequest { Cwd = "/tmp", McpServers = new List<McpServer>() },
            cts.Token);

        var promptTask = client.RequestAsync<PromptRequest, PromptResponse>(
            "session/prompt",
            new PromptRequest { SessionId = newSes.SessionId, Prompt = new List<ContentBlock>() },
            cts.Token);

        // Give the agent time to start.
        await Task.Delay(100, cts.Token);

        // Cancel the session.
        var cancelJson = System.Text.Json.JsonSerializer.Serialize(new CancelNotification { SessionId = newSes.SessionId }, Agent.Acp.Protocol.AcpJson.Options);
        using var cancelDoc = System.Text.Json.JsonDocument.Parse(cancelJson);

        await clientTransport.SendMessageAsync(new Agent.Acp.Protocol.JsonRpcNotification
        {
            Method = "session/cancel",
            Params = cancelDoc.RootElement.Clone(),
        }, cts.Token);

        // Prompt should finish (not hang until timeout).
        var completed = await Task.WhenAny(promptTask, Task.Delay(2000, cts.Token));
        Assert.Same(promptTask, completed);

        cts.Cancel();
        try { await serverTask; } catch { }
    }

    private sealed class BlockingFactory : IAcpAgentFactory
    {
        public Task<InitializeResponse> InitializeAsync(InitializeRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new InitializeResponse
            {
                ProtocolVersion = request.ProtocolVersion,
                AgentInfo = new AgentInfo { AdditionalProperties = new Dictionary<string, object> { ["name"] = "agent", ["version"] = "0" } },
                AgentCapabilities = new AgentCapabilities(),
                AuthMethods = new List<AuthMethod>(),
            });

        public Task<NewSessionResponse> NewSessionAsync(NewSessionRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new NewSessionResponse { SessionId = "ses_test", Modes = new Modes2() });

        public IAcpSessionAgent CreateSessionAgent(string sessionId, IAcpClientCaller client, IAcpSessionEvents events)
            => new BlockingSessionAgent();

        private sealed class BlockingSessionAgent : IAcpSessionAgent
        {
            public async Task<PromptResponse> PromptAsync(PromptRequest request, IAcpPromptTurn turn, CancellationToken cancellationToken)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new PromptResponse();
            }
        }
    }
}
