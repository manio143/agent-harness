using Agent.Acp.Acp;
using Agent.Acp.Protocol;
using Agent.Acp.Schema;

namespace Agent.Acp.Tests;

public class AcpPromptCancellationSemanticsTests
{
    [Fact]
    public async Task Prompt_WhenCancelled_MustReturn_StopReasonCancelled_NotJsonRpcError()
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();

        var server = new AcpAgentServer(new BlockingFactory());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverTask = Task.Run(() => server.RunAsync(serverTransport, cts.Token), cts.Token);

        await using var client = new AcpClientConnection(clientTransport);

        _ = await client.RequestAsync<InitializeRequest, InitializeResponse>(
            "initialize",
            new InitializeRequest { ProtocolVersion = 1, ClientInfo = new ClientInfo { AdditionalProperties = new Dictionary<string, object> { ["name"] = "test", ["version"] = "0" } }, ClientCapabilities = new ClientCapabilities() },
            cts.Token);

        var newSes = await client.RequestAsync<NewSessionRequest, NewSessionResponse>(
            "session/new",
            new NewSessionRequest { Cwd = "/tmp", McpServers = new List<McpServer>() },
            cts.Token);

        var promptTask = client.RequestAsync<PromptRequest, PromptResponse>(
            "session/prompt",
            new PromptRequest { SessionId = newSes.SessionId, Prompt = new List<ContentBlock>() },
            cts.Token);

        // Give prompt time to start.
        await Task.Delay(100, cts.Token);

        // Send cancel notification.
        var cancelJson = System.Text.Json.JsonSerializer.Serialize(new CancelNotification { SessionId = newSes.SessionId }, AcpJson.Options);
        using var cancelDoc = System.Text.Json.JsonDocument.Parse(cancelJson);

        await clientTransport.SendMessageAsync(new JsonRpcNotification
        {
            Method = "session/cancel",
            Params = cancelDoc.RootElement.Clone(),
        }, cts.Token);

        var resp = await promptTask;
        Assert.Equal(StopReason.Cancelled, resp.StopReason.Value);

        cts.Cancel();
        try { await serverTask; } catch { }
    }

    private sealed class BlockingFactory : IAcpAgentFactory
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
            new BlockingSessionAgent();

        private sealed class BlockingSessionAgent : IAcpSessionAgent
        {
            public async Task<PromptResponse> PromptAsync(PromptRequest request, IAcpPromptTurn turn, CancellationToken cancellationToken)
            {
                // Simulate a cooperative cancellation-sensitive operation.
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new PromptResponse { StopReason = StopReason.EndTurn };
            }
        }
    }
}
