using System.Text.Json;
using Agent.Acp.Acp;
using Agent.Acp.Protocol;
using Agent.Acp.Schema;

namespace Agent.Acp.Tests;

public class AcpPromptUpdateTests
{
    [Fact]
    public async Task SessionPrompt_Emits_SessionUpdate_Notifications()
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();

        var agent = new UpdatingAgent();
        var server = new AcpAgentServer(agent);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverTask = Task.Run(() => server.RunAsync(serverTransport, cts.Token), cts.Token);

        var updates = new List<JsonRpcNotification>();

        await using var client = new AcpClientConnection(clientTransport);
        client.NotificationReceived += n =>
        {
            if (n.Method == "session/update") updates.Add(n);
        };

        // Initialize + new session first.
        _ = await client.RequestAsync<InitializeRequest, InitializeResponse>(
            "initialize",
            new InitializeRequest
            {
                ProtocolVersion = 1,
                ClientInfo = new ClientInfo { AdditionalProperties = new Dictionary<string, object> { ["name"] = "test", ["version"] = "0" } },
                ClientCapabilities = new ClientCapabilities { Fs = new FileSystemCapabilities { ReadTextFile = true, WriteTextFile = false } },
            },
            cts.Token);

        var newSes = await client.RequestAsync<NewSessionRequest, NewSessionResponse>(
            "session/new",
            new NewSessionRequest { Cwd = "/tmp", McpServers = new List<McpServer>() },
            cts.Token);

        // Run prompt; agent will emit updates.
        _ = await client.RequestAsync<PromptRequest, PromptResponse>(
            "session/prompt",
            new PromptRequest
            {
                SessionId = newSes.SessionId,
                Prompt = new List<Content1>(),
            },
            cts.Token);

        // Wait a moment for collector.
        await Task.Delay(100, cts.Token);

        Assert.True(updates.Count >= 2, $"Expected at least 2 session/update notifications, got {updates.Count}");

        // Validate the notification params includes sessionId.
        var first = updates[0];
        Assert.NotNull(first.Params);
        var raw = first.Params!.Value;
        Assert.Equal(newSes.SessionId, raw.GetProperty("sessionId").GetString());

        cts.Cancel();
        try { await serverTask; } catch { }
    }

    private sealed class UpdatingAgent : IAcpAgentWithContext
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

        public Task<PromptResponse> PromptAsync(PromptRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new PromptResponse());

        public async Task<PromptResponse> PromptAsync(PromptRequest request, IAcpAgentContext context, CancellationToken cancellationToken)
        {
            await context.SendSessionUpdateAsync(new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = "hello" } }, cancellationToken);
            await context.SendSessionUpdateAsync(new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = "world" } }, cancellationToken);
            return new PromptResponse { StopReason = StopReason2.EndTurn };
        }
    }
}
