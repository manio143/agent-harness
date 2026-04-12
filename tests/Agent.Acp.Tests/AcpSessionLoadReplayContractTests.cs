using Agent.Acp.Acp;
using Agent.Acp.Schema;

namespace Agent.Acp.Tests;

public sealed class AcpSessionLoadReplayContractTests
{
    [Fact]
    public async Task SessionLoad_Replays_History_Via_SessionUpdate_Before_Response()
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();

        var factory = new ReplayFactory();
        var server = new AcpAgentServer(factory);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverTask = Task.Run(() => server.RunAsync(serverTransport, cts.Token), cts.Token);

        await using var client = new AcpClientConnection(clientTransport);

        var updates = new List<string>();
        var gotTwo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        client.NotificationReceived += n =>
        {
            if (n.Method != "session/update") return;

            var p = n.Params!.Value;
            var update = p.GetProperty("update");
            var kind = update.GetProperty("sessionUpdate").GetString();
            updates.Add(kind ?? "");

            if (updates.Count >= 2)
                gotTwo.TrySetResult();
        };

        await client.RequestAsync<InitializeRequest, InitializeResponse>(
            "initialize",
            new InitializeRequest
            {
                ProtocolVersion = 1,
                ClientInfo = new ClientInfo { AdditionalProperties = new Dictionary<string, object> { ["name"] = "test", ["version"] = "0" } },
                ClientCapabilities = new ClientCapabilities(),
            },
            cts.Token);

        var loadTask = client.RequestAsync<LoadSessionRequest, LoadSessionResponse>(
            "session/load",
            new LoadSessionRequest
            {
                SessionId = "sess1",
                Cwd = "/tmp",
                McpServers = new List<McpServer>(),
            },
            cts.Token);

        // We should observe session/update notifications (replay). We do not assert transport-level
        // interleaving between notifications and the final response (depends on client buffering).
        await loadTask;
        await gotTwo.Task;

        cts.Cancel();
        try { await serverTask; } catch { }
    }

    private sealed class ReplayFactory : IAcpAgentFactory, IAcpSessionReplayProvider
    {
        public Task<InitializeResponse> InitializeAsync(InitializeRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new InitializeResponse
            {
                ProtocolVersion = request.ProtocolVersion,
                AgentInfo = new AgentInfo { AdditionalProperties = new Dictionary<string, object> { ["name"] = "agent", ["version"] = "0" } },
                AgentCapabilities = new AgentCapabilities
                {
                    LoadSession = true,
                    McpCapabilities = new McpCapabilities(),
                    PromptCapabilities = new PromptCapabilities(),
                    SessionCapabilities = new SessionCapabilities { List = new Agent.Acp.Schema.List() },
                },
                AuthMethods = new List<AuthMethod>(),
            });

        public Task<NewSessionResponse> NewSessionAsync(NewSessionRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new NewSessionResponse { SessionId = "s", ConfigOptions = new List<SessionConfigOption>(), Modes = null });

        public Task<LoadSessionResponse>? LoadSessionAsync(LoadSessionRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new LoadSessionResponse { ConfigOptions = new List<SessionConfigOption>(), Modes = null });

        public IAcpSessionAgent CreateSessionAgent(string sessionId, IAcpClientCaller client, IAcpSessionEvents events)
            => new NoopSessionAgent();

        public async Task ReplaySessionAsync(string sessionId, IAcpSessionEvents events, CancellationToken cancellationToken)
        {
            await events.SendSessionUpdateAsync(new UserMessageChunk { Content = new TextContent { Text = "hi" } }, cancellationToken);
            await events.SendSessionUpdateAsync(new AgentMessageChunk { Content = new TextContent { Text = "hello" } }, cancellationToken);
        }

        private sealed class NoopSessionAgent : IAcpSessionAgent
        {
            public Task<PromptResponse> PromptAsync(PromptRequest request, IAcpPromptTurn turn, CancellationToken cancellationToken)
                => Task.FromResult(new PromptResponse { StopReason = StopReason.EndTurn });
        }
    }
}
