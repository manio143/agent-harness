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
using MeaiChatResponseUpdate = Microsoft.Extensions.AI.ChatResponseUpdate;
using MeaiChatResponse = Microsoft.Extensions.AI.ChatResponse;
using MeaiChatOptions = Microsoft.Extensions.AI.ChatOptions;
using MeaiAIContent = Microsoft.Extensions.AI.AIContent;
using MeaiFunctionCallContent = Microsoft.Extensions.AI.FunctionCallContent;
using MeaiTextContent = Microsoft.Extensions.AI.TextContent;

namespace Agent.Harness.Tests;

public sealed class AcpEnqueueWakeRegressionIntegrationTests
{
    [Fact]
    public async Task EnqueueDelivery_SchedulesFollowupModelCall_WithoutSecondPrompt()
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();
        var factory = new Factory();
        var server = new AcpAgentServer(factory);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverTask = Task.Run(() => server.RunAsync(serverTransport, cts.Token), cts.Token);

        var updates = new List<JsonRpcNotification>();

        await using var client = new AcpClientConnection(clientTransport);
        client.NotificationReceived += n => { if (n.Method == "session/update") updates.Add(n); };

        client.RequestHandler = (req, _) => throw new InvalidOperationException($"Unexpected request: {req.Method}");

        _ = await client.RequestAsync<InitializeRequest, InitializeResponse>(
            "initialize",
            new InitializeRequest
            {
                ProtocolVersion = 1,
                ClientInfo = new ClientInfo { AdditionalProperties = new Dictionary<string, object> { ["name"] = "test", ["version"] = "0" } },
                ClientCapabilities = new ClientCapabilities { Fs = new FileSystemCapabilities() },
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
                Prompt = new List<ContentBlock> { new Agent.Acp.Schema.TextContent { Text = "Hi" } },
            },
            cts.Token);

        // Wait for wake follow-up to be processed.
        await Task.Delay(250, cts.Token);

        var chunks = updates
            .Select(n => JsonSerializer.Deserialize<SessionNotification>(n.Params?.GetRawText() ?? "{}", AcpJson.Options))
            .Where(p => p?.Update is AgentMessageChunk)
            .Select(p => ((AgentMessageChunk)p!.Update).Content)
            .OfType<Agent.Acp.Schema.TextContent>()
            .Select(t => t.Text)
            .ToList();

        chunks.Should().Contain("Followup");

        cts.Cancel();
        try { await serverTask; } catch { }
    }

    private sealed class Factory : IAcpAgentFactory
    {
        public Task<InitializeResponse> InitializeAsync(InitializeRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new InitializeResponse
            {
                ProtocolVersion = request.ProtocolVersion,
                AgentInfo = new AgentInfo { AdditionalProperties = new Dictionary<string, object> { ["name"] = "harness-test-agent", ["version"] = "0" } },
                AgentCapabilities = new AgentCapabilities { PromptCapabilities = new PromptCapabilities(), LoadSession = false },
                AuthMethods = new List<AuthMethod>(),
            });

        public Task<NewSessionResponse> NewSessionAsync(NewSessionRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new NewSessionResponse { SessionId = "ses_enqueue_reg", Modes = null, ConfigOptions = new List<SessionConfigOption>() });

        public IAcpSessionAgent CreateSessionAgent(string sessionId, IAcpClientCaller client, IAcpSessionEvents events)
        {
            var dir = Path.Combine(Path.GetTempPath(), "harness-acp-enqueue-tests", Guid.NewGuid().ToString("N"));
            var store = new JsonlSessionStore(dir);
            store.CreateNew(sessionId, new SessionMetadata(
                SessionId: sessionId,
                Cwd: "/tmp",
                Title: "",
                CreatedAtIso: "2026-04-12T00:00:00Z",
                UpdatedAtIso: "2026-04-12T00:00:00Z"));

            var coreOptions = new CoreOptions { CommitAssistantTextDeltas = true };
            var publishOptions = new AcpPublishOptions(PublishReasoning: false);

            var chat = new ScriptedMeaiChatClient();
            var initialState = SessionState.Empty;

            return new HarnessAcpSessionAgent(sessionId, client, chat, events, coreOptions, publishOptions, store, initialState);
        }
    }

    private sealed class ScriptedMeaiChatClient : MeaiIChatClient
    {
        private int _promptCalls;

        public IAsyncEnumerable<MeaiChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<MeaiChatMessage> messages, MeaiChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            // Distinguish warmup from real prompt/wake calls.
            var isPromptish = messages.Any(m => m.Role == Microsoft.Extensions.AI.ChatRole.User);
            if (!isPromptish)
                return Warmup();

            _promptCalls++;

            return _promptCalls switch
            {
                1 => Call1_Enqueue(),
                2 => Call2_Tools(),
                _ => Call3_Text(),
            };
        }

        private async IAsyncEnumerable<MeaiChatResponseUpdate> Warmup()
        {
            yield return new MeaiChatResponseUpdate { Contents = new List<MeaiAIContent> { new MeaiTextContent("(warmup)") } };
        }

        private async IAsyncEnumerable<MeaiChatResponseUpdate> Call1_Enqueue()
        {
            yield return new MeaiChatResponseUpdate
            {
                Contents = new List<MeaiAIContent>
                {
                    new MeaiFunctionCallContent("callA0", "report_intent", new Dictionary<string, object?> { ["intent"] = "enqueue" }),
                    new MeaiFunctionCallContent("callA1", "thread_send", new Dictionary<string, object?> { ["threadId"] = "main", ["message"] = "wake", ["delivery"] = "enqueue" }),
                }
            };

            yield return new MeaiChatResponseUpdate
            {
                Contents = new List<MeaiAIContent> { new MeaiTextContent(".") }
            };
        }

        private async IAsyncEnumerable<MeaiChatResponseUpdate> Call2_Tools()
        {
            // Follow-up wake: choose an intent and list threads.
            yield return new MeaiChatResponseUpdate
            {
                Contents = new List<MeaiAIContent>
                {
                    new MeaiFunctionCallContent("callB0", "report_intent", new Dictionary<string, object?> { ["intent"] = "process" }),
                    new MeaiFunctionCallContent("callB1", "thread_list", new Dictionary<string, object?>()),
                }
            };
        }

        private async IAsyncEnumerable<MeaiChatResponseUpdate> Call3_Text()
        {
            yield return new MeaiChatResponseUpdate
            {
                Contents = new List<MeaiAIContent> { new MeaiTextContent("Followup") }
            };
        }

        public Task<MeaiChatResponse> GetResponseAsync(IEnumerable<MeaiChatMessage> messages, MeaiChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new MeaiChatResponse(Array.Empty<MeaiChatMessage>()));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }

        public Task<string> CompleteAsync(IReadOnlyList<MeaiChatMessage> renderedMessages, CancellationToken cancellationToken)
            => Task.FromResult("");
    }
}
