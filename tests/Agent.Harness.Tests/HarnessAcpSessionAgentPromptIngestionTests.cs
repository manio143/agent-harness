using System.Collections.Immutable;
using System.Text.Json;
using Agent.Acp.Acp;
using Agent.Acp.Protocol;
using Agent.Acp.Schema;
using Agent.Acp.Tests;
using Agent.Harness.Acp;
using Agent.Harness.Persistence;
using FluentAssertions;

using MeaiIChatClient = Microsoft.Extensions.AI.IChatClient;
using MeaiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using MeaiChatResponse = Microsoft.Extensions.AI.ChatResponse;
using MeaiChatResponseUpdate = Microsoft.Extensions.AI.ChatResponseUpdate;
using MeaiChatOptions = Microsoft.Extensions.AI.ChatOptions;
using MeaiAIContent = Microsoft.Extensions.AI.AIContent;
using MeaiTextContent = Microsoft.Extensions.AI.TextContent;

namespace Agent.Harness.Tests;

public sealed class HarnessAcpSessionAgentPromptIngestionTests
{
    [Fact]
    public async Task SessionPrompt_Projects_AllTextBlocks_AndResourceLinks_IntoUserText()
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();
        var factory = new Factory();
        var server = new AcpAgentServer(factory);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverTask = Task.Run(() => server.RunAsync(serverTransport, cts.Token), cts.Token);

        await using var client = new AcpClientConnection(clientTransport);

        _ = await client.RequestAsync<InitializeRequest, InitializeResponse>(
            "initialize",
            new InitializeRequest
            {
                ProtocolVersion = 1,
                ClientInfo = new ClientInfo { AdditionalProperties = new Dictionary<string, object> { ["name"] = "test", ["version"] = "0" } },
                ClientCapabilities = new ClientCapabilities { Fs = new FileSystemCapabilities { ReadTextFile = true } },
            },
            cts.Token);

        var ses = await client.RequestAsync<NewSessionRequest, NewSessionResponse>(
            "session/new",
            new NewSessionRequest { Cwd = "/tmp", McpServers = new List<McpServer>() },
            cts.Token);

        _ = await client.RequestAsync<PromptRequest, PromptResponse>(
            "session/prompt",
            new PromptRequest
            {
                SessionId = ses.SessionId,
                Prompt = new List<ContentBlock>
                {
                    new ResourceLink { Name = "spec", Uri = "file:///tmp/spec.md", Title = "Spec" },
                    new Agent.Acp.Schema.TextContent { Text = "Hello" },
                    new Agent.Acp.Schema.TextContent { Text = "World" },
                },
            },
            cts.Token);

        // Assert: MEAI saw a user message that included BOTH the resource link AND later text blocks.
        factory.Chat.LastUserText.Should().Contain("resource_link");
        factory.Chat.LastUserText.Should().Contain("file:///tmp/spec.md");
        factory.Chat.LastUserText.Should().Contain("Hello");
        factory.Chat.LastUserText.Should().Contain("World");

        cts.Cancel();
        try { await serverTask; } catch { }
    }

    private sealed class Factory : IAcpAgentFactory
    {
        private readonly RecordingChatClient _chat = new();
        public RecordingChatClient Chat => _chat;

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
        {
            // In-memory session store.
            var dir = Path.Combine(Path.GetTempPath(), "harness-acp-agent-tests", Guid.NewGuid().ToString("N"));
            var store = new JsonlSessionStore(dir);
            store.CreateNew(sessionId, new SessionMetadata(
                SessionId: sessionId,
                Cwd: "/tmp",
                Title: "",
                CreatedAtIso: "2026-04-12T00:00:00Z",
                UpdatedAtIso: "2026-04-12T00:00:00Z"));

            var coreOptions = new CoreOptions();
            var publishOptions = new AcpPublishOptions(PublishReasoning: false);

            // Tool catalog is irrelevant for this test.
            var initialState = SessionState.Empty with { Tools = ImmutableArray<ToolDefinition>.Empty };

            return new HarnessAcpSessionAgent(sessionId, client, _chat, events, coreOptions, publishOptions, store, initialState);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }

    private sealed class RecordingChatClient : MeaiIChatClient
    {
        public string LastUserText { get; private set; } = "";

        public async IAsyncEnumerable<MeaiChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<MeaiChatMessage> messages, MeaiChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastUserText = messages.LastOrDefault(m => m.Role.Value == "user")?.Text ?? "";

            yield return new MeaiChatResponseUpdate
            {
                Contents = new List<MeaiAIContent> { new MeaiTextContent("ok") }
            };
        }

        public Task<MeaiChatResponse> GetResponseAsync(IEnumerable<MeaiChatMessage> messages, MeaiChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new MeaiChatResponse(Array.Empty<MeaiChatMessage>()));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
