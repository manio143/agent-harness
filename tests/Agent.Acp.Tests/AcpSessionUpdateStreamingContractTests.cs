using Agent.Acp.Acp;
using Agent.Acp.Protocol;
using Agent.Acp.Schema;

namespace Agent.Acp.Tests;

public class AcpSessionUpdateStreamingContractTests
{
    [Fact]
    public async Task SessionPrompt_Can_Stream_Multiple_SessionUpdates_Before_Final_Response()
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();
        var server = new AcpAgentServer(new Factory());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverTask = Task.Run(() => server.RunAsync(serverTransport, cts.Token), cts.Token);

        await using var client = new AcpClientConnection(clientTransport);

        var updatesReceived = 0;
        var gotThreeUpdates = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        client.NotificationReceived += n =>
        {
            if (n.Method != "session/update")
                return;

            if (Interlocked.Increment(ref updatesReceived) >= 3)
                gotThreeUpdates.TrySetResult();
        };

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

        var responseTask = client.RequestAsync<PromptRequest, PromptResponse>(
            "session/prompt",
            new PromptRequest { SessionId = ses.SessionId, Prompt = new List<ContentBlock>() },
            cts.Token);

        var resp = await responseTask;

        // Contract: a prompt turn can emit multiple session/update notifications,
        // and those notifications must be delivered *before* the final prompt response.
        // (With the in-memory channel transport, message order is preserved.)
        Assert.True(gotThreeUpdates.Task.IsCompletedSuccessfully, "Expected session/update notifications before the prompt response completed");
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
            Task.FromResult(new NewSessionResponse { SessionId = "ses_test", Modes = null });

        public IAcpSessionAgent CreateSessionAgent(string sessionId, IAcpClientCaller client, IAcpSessionEvents events) =>
            new Agent(events);

        private sealed class Agent : IAcpSessionAgent
        {
            private readonly IAcpSessionEvents _events;

            public Agent(IAcpSessionEvents events)
            {
                _events = events;
            }

            public async Task<PromptResponse> PromptAsync(PromptRequest request, IAcpPromptTurn turn, CancellationToken cancellationToken)
            {
                await _events.SendSessionUpdateAsync(new AgentMessageChunk { Content = new TextContent { Text = "one" } }, cancellationToken);
                await _events.SendSessionUpdateAsync(new AgentMessageChunk { Content = new TextContent { Text = "two" } }, cancellationToken);
                await _events.SendSessionUpdateAsync(new AgentMessageChunk { Content = new TextContent { Text = "three" } }, cancellationToken);

                return new PromptResponse { StopReason = StopReason.EndTurn };
            }
        }
    }
}
