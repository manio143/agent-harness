using System.Text.Json;
using Agent.Acp.Acp;
using Agent.Acp.Protocol;
using Agent.Acp.Schema;
using Agent.Acp.Tests;
using Agent.Harness;
using Agent.Harness.Acp;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class AcpAdapterReasoningPublishingTests
{
    [Fact]
    public async Task SessionPrompt_WhenReasoningCommitted_ButPublishDisabled_DoesNotSendThoughtChunks()
    {
        static async IAsyncEnumerable<ObservedChatEvent> Observed(PromptRequest _)
        {
            yield return new ObservedUserMessage("Hello");
            yield return new ObservedReasoningTextDelta("thinking...");
            await Task.Yield();
            yield return new ObservedAssistantTextDelta("Hello back");
            yield return new ObservedAssistantMessageCompleted();
        }

        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();
        var server = new AcpAgentServer(new Factory(Observed, publishReasoning: false));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverTask = Task.Run(() => server.RunAsync(serverTransport, cts.Token), cts.Token);

        await using var client = new AcpClientConnection(clientTransport);

        var updates = new List<SessionUpdate>();
        client.NotificationReceived += n =>
        {
            if (n.Method != "session/update") return;
            var payload = JsonSerializer.Deserialize<SessionNotification>(n.Params?.GetRawText() ?? "{}", AcpJson.Options);
            if (payload is not null) updates.Add(payload.Update);
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

        _ = await client.RequestAsync<PromptRequest, PromptResponse>(
            "session/prompt",
            new PromptRequest
            {
                SessionId = newSession.SessionId,
                Prompt = new List<ContentBlock> { new TextContent { Text = "Hello" } },
            },
            cts.Token);

        updates.OfType<AgentThoughtChunk>().Should().BeEmpty();

        cts.Cancel();
        try { await serverTask; } catch { }
    }

    [Fact]
    public async Task SessionPrompt_WhenReasoningCommitted_AndPublishEnabled_SendsThoughtChunks()
    {
        static async IAsyncEnumerable<ObservedChatEvent> Observed(PromptRequest _)
        {
            yield return new ObservedUserMessage("Hello");
            yield return new ObservedReasoningTextDelta("thinking...");
            await Task.Yield();
            yield return new ObservedAssistantTextDelta("Hello back");
            yield return new ObservedAssistantMessageCompleted();
        }

        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();
        var server = new AcpAgentServer(new Factory(Observed, publishReasoning: true));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverTask = Task.Run(() => server.RunAsync(serverTransport, cts.Token), cts.Token);

        await using var client = new AcpClientConnection(clientTransport);

        var updates = new List<SessionUpdate>();
        client.NotificationReceived += n =>
        {
            if (n.Method != "session/update") return;
            var payload = JsonSerializer.Deserialize<SessionNotification>(n.Params?.GetRawText() ?? "{}", AcpJson.Options);
            if (payload is not null) updates.Add(payload.Update);
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

        _ = await client.RequestAsync<PromptRequest, PromptResponse>(
            "session/prompt",
            new PromptRequest
            {
                SessionId = newSession.SessionId,
                Prompt = new List<ContentBlock> { new TextContent { Text = "Hello" } },
            },
            cts.Token);

        var thoughts = updates.OfType<AgentThoughtChunk>().ToList();
        thoughts.Should().HaveCount(1);
        thoughts[0].Content.Should().BeOfType<TextContent>().Which.Text.Should().Be("thinking...");

        cts.Cancel();
        try { await serverTask; } catch { }
    }

    private sealed class Factory : IAcpAgentFactory
    {
        private readonly Func<PromptRequest, IAsyncEnumerable<ObservedChatEvent>> _observed;
        private readonly bool _publishReasoning;

        public Factory(Func<PromptRequest, IAsyncEnumerable<ObservedChatEvent>> observed, bool publishReasoning)
        {
            _observed = observed;
            _publishReasoning = publishReasoning;
        }

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
            => new AcpSessionAgentAdapter(
                sessionId,
                events,
                _observed,
                coreOptions: new CoreOptions(CommitReasoningTextDeltas: true),
                publishOptions: new AcpPublishOptions(PublishReasoning: _publishReasoning));
    }
}
