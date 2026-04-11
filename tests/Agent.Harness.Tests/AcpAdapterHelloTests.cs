using System.Text.Json;
using Agent.Acp.Acp;
using Agent.Acp.Protocol;
using Agent.Acp.Schema;
using Agent.Acp.Tests;
using Agent.Harness;
using Agent.Harness.Acp;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class AcpAdapterHelloTests
{
    [Fact]
    public async Task GivenHello_WhenPrompted_EmitsAgentMessageChunkAndEndTurn_AndLogsEvents()
    {
        // Arrange: core deps
        var log = new InMemoryEventLog();
        var chat = new ScriptedChatClient().WhenCalledReturn("Hello back");
        var core = new SessionCore(log, chat);

        // Arrange: ACP harness
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();
        var server = new AcpAgentServer(new Factory(core));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverTask = Task.Run(() => server.RunAsync(serverTransport, cts.Token), cts.Token);

        await using var client = new AcpClientConnection(clientTransport);

        var updates = new List<SessionUpdate>();
        client.NotificationReceived += n =>
        {
            if (n.Method != "session/update")
                return;

            var payload = JsonSerializer.Deserialize<SessionNotification>(n.Params?.GetRawText() ?? "{}", AcpJson.Options);
            if (payload is not null)
                updates.Add(payload.Update);
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

        var newSession = await client.RequestAsync<NewSessionRequest, NewSessionResponse>(
            "session/new",
            new NewSessionRequest { Cwd = "/tmp", McpServers = new List<McpServer>() },
            cts.Token);

        // Act
        var resp = await client.RequestAsync<PromptRequest, PromptResponse>(
            "session/prompt",
            new PromptRequest
            {
                SessionId = newSession.SessionId,
                Prompt = new List<ContentBlock> { new TextContent { Text = "Hello" } },
            },
            cts.Token);

        // Assert: response
        resp.StopReason.Value.Should().Be(StopReason.EndTurn);

        // Assert: streaming update included assistant text
        updates.OfType<AgentMessageChunk>()
            .Any(chunk => chunk.Content is TextContent t && t.Text == "Hello back")
            .Should().BeTrue();

        // Assert: typed event log
        log.Events.Should().Contain(new UserMessageAdded("Hello"));
        log.Events.Should().Contain(new AssistantMessageAdded("Hello back"));

        // Assert: chat client invoked with user text
        chat.Calls.Should().ContainSingle();
        chat.Calls[0].Should().Equal(new ChatMessage(ChatRole.User, "Hello"));

        cts.Cancel();
        try { await serverTask; } catch { }
    }

    private sealed class Factory : IAcpAgentFactory
    {
        private readonly SessionCore _core;

        public Factory(SessionCore core) => _core = core;

        public Task<InitializeResponse> InitializeAsync(InitializeRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new InitializeResponse
            {
                ProtocolVersion = request.ProtocolVersion,
                AgentInfo = new AgentInfo { AdditionalProperties = new Dictionary<string, object> { ["name"] = "harness-test-agent", ["version"] = "0" } },
                AgentCapabilities = new AgentCapabilities { PromptCapabilities = new PromptCapabilities(), LoadSession = false },
                AuthMethods = new List<AuthMethod>(),
            });

        public Task<NewSessionResponse> NewSessionAsync(NewSessionRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new NewSessionResponse { SessionId = "ses_test", Modes = null, ConfigOptions = new List<SessionConfigOption>() });

        public IAcpSessionAgent CreateSessionAgent(string sessionId, IAcpClientCaller client, IAcpSessionEvents events)
            => new AcpSessionAgentAdapter(_core, events);
    }
}
