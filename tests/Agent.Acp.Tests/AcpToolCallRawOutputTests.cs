using System.Text.Json;
using Agent.Acp.Acp;
using Agent.Acp.Protocol;
using Agent.Acp.Schema;

namespace Agent.Acp.Tests;

public sealed class AcpToolCallRawOutputTests
{
    [Fact]
    public async Task ToolCall_Completed_Emits_rawOutput_On_session_update()
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();

        var server = new AcpAgentServer(new Factory());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverTask = Task.Run(() => server.RunAsync(serverTransport, cts.Token), cts.Token);

        var updates = new List<JsonRpcNotification>();

        await using var client = new AcpClientConnection(clientTransport);
        client.NotificationReceived += n =>
        {
            if (n.Method == "session/update") updates.Add(n);
        };

        _ = await client.RequestAsync<InitializeRequest, InitializeResponse>(
            "initialize",
            new InitializeRequest
            {
                ProtocolVersion = 1,
                ClientInfo = new ClientInfo { AdditionalProperties = new Dictionary<string, object> { ["name"] = "test", ["version"] = "0" } },
                ClientCapabilities = new ClientCapabilities { Fs = new FileSystemCapabilities() },
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

        await Task.Delay(50, cts.Token);

        var toolUpdates = updates
            .Select(n => JsonSerializer.Deserialize<SessionNotification>(n.Params?.GetRawText() ?? "{}", AcpJson.Options))
            .Where(p => p?.Update is ToolCallUpdate)
            .Select(p => (ToolCallUpdate)p!.Update)
            .ToList();

        Assert.Contains(toolUpdates, u => u.ToolCallId == "call_1" && u.Status.Value == ToolCallStatus.Completed);

        var completed = toolUpdates.Last(u => u.ToolCallId == "call_1" && u.Status.Value == ToolCallStatus.Completed);
        var rawJson = JsonSerializer.Serialize(completed.RawOutput, AcpJson.Options);
        using var doc = JsonDocument.Parse(rawJson);
        Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());

        cts.Cancel();
        try { await serverTask; } catch { }
    }

    private sealed class Factory : IAcpAgentFactory
    {
        public Task<InitializeResponse> InitializeAsync(InitializeRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new InitializeResponse
            {
                ProtocolVersion = request.ProtocolVersion,
                AgentInfo = new AgentInfo { AdditionalProperties = new Dictionary<string, object> { ["name"] = "agent", ["version"] = "0" } },
                AgentCapabilities = new AgentCapabilities { PromptCapabilities = new PromptCapabilities() },
                AuthMethods = new List<AuthMethod>(),
            });

        public Task<NewSessionResponse> NewSessionAsync(NewSessionRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new NewSessionResponse { SessionId = "ses_test", Modes = null });

        public IAcpSessionAgent CreateSessionAgent(string sessionId, IAcpClientCaller client, IAcpSessionEvents events)
            => new Session(events);

        private sealed class Session : IAcpSessionAgent
        {
            private readonly IAcpSessionEvents _events;
            public Session(IAcpSessionEvents events) { _events = events; }

            public async Task<PromptResponse> PromptAsync(PromptRequest request, IAcpPromptTurn turn, CancellationToken cancellationToken)
            {
                var call = turn.ToolCalls.Start("call_1", "test", ToolKind.Other);
                await call.InProgressAsync(cancellationToken);

                await call.CompletedAsync(cancellationToken, rawOutput: new { status = "ok" });
                return new PromptResponse { StopReason = StopReason.EndTurn };
            }
        }
    }
}
