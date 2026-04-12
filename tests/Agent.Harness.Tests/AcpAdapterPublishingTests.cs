using System.Text.Json;
using Agent.Acp.Acp;
using Agent.Acp.Protocol;
using Agent.Acp.Schema;
using Agent.Acp.Tests;
using Agent.Harness;
using Agent.Harness.Acp;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class AcpAdapterPublishingTests
{
    [Fact]
    public async Task SessionPrompt_PublishesOnlyCommittedEvents_NoDeltaLeakage()
    {
        // Arrange: observed stream includes deltas before completion.
        static async IAsyncEnumerable<ObservedChatEvent> Observed(PromptRequest _)
        {
            yield return new ObservedUserMessage("Hello") { RawUpdate = "raw-user" };
            yield return new ObservedAssistantTextDelta("Hello") { RawUpdate = "raw-1" };
            yield return new ObservedAssistantTextDelta(" back") { RawUpdate = "raw-2" };
            await Task.Yield();
            yield return new ObservedAssistantMessageCompleted("stop") { RawUpdate = "raw-done" };
        }

        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();
        var server = new AcpAgentServer(new Factory(Observed));

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

        // Only committed assistant message should be published => single chunk.
        var chunks = updates.OfType<AgentMessageChunk>().ToList();
        chunks.Should().HaveCount(1);

        var chunk = chunks.Single();
        chunk.Content.Should().BeOfType<TextContent>().Which.Text.Should().Be("Hello back");

        cts.Cancel();
        try { await serverTask; } catch { }
    }

    private sealed class Factory : IAcpAgentFactory
    {
        private readonly Func<PromptRequest, IAsyncEnumerable<ObservedChatEvent>> _observed;

        public Factory(Func<PromptRequest, IAsyncEnumerable<ObservedChatEvent>> observed) => _observed = observed;

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
            => new AcpSessionAgentAdapter(sessionId, events, _observed);
    }
}
