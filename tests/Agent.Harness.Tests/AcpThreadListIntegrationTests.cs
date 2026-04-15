using System.Text.Json;
using Agent.Acp.Acp;
using Agent.Acp.Schema;
using Agent.Acp.Tests;
using Agent.Harness.Acp;
using Agent.Harness.Persistence;
using FluentAssertions;
using System.Collections.Immutable;

using MeaiIChatClient = Microsoft.Extensions.AI.IChatClient;
using MeaiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using MeaiChatResponseUpdate = Microsoft.Extensions.AI.ChatResponseUpdate;
using MeaiChatResponse = Microsoft.Extensions.AI.ChatResponse;
using MeaiChatOptions = Microsoft.Extensions.AI.ChatOptions;
using MeaiAIContent = Microsoft.Extensions.AI.AIContent;
using MeaiFunctionCallContent = Microsoft.Extensions.AI.FunctionCallContent;
using MeaiTextContent = Microsoft.Extensions.AI.TextContent;

namespace Agent.Harness.Tests;

public sealed class AcpThreadListIntegrationTests
{
    [Fact]
    public async Task ThreadList_Reflects_ReportIntent_ForMainThread()
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();

        var factory = new Factory();
        var server = new AcpAgentServer(factory);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverTask = Task.Run(() => server.RunAsync(serverTransport, cts.Token), cts.Token);

        await using var client = new AcpClientConnection(clientTransport);

        // Client doesn't need to handle anything; we only call internal tools.
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

        // Assert thread metadata was updated by report_intent and is visible for thread_list.
        // (Tool raw outputs are not currently surfaced over ACP, so we validate persistence.)
        var metaPath = Path.Combine(factory.RootDir, newSession.SessionId, "threads", "main", "thread.json");
        File.Exists(metaPath).Should().BeTrue($"Expected thread metadata at {metaPath}");

        var sessionEventsPath = Path.Combine(factory.RootDir, newSession.SessionId, "events.jsonl");
        var sessionEvents = File.Exists(sessionEventsPath) ? File.ReadAllText(sessionEventsPath) : "<missing>";

        using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
        doc.RootElement.GetProperty("intent").GetString().Should().Be("list threads", $"session events: {sessionEvents}");

        var eventsPath = Path.Combine(factory.RootDir, newSession.SessionId, "threads", "main", "events.jsonl");
        File.ReadAllText(eventsPath).Should().Contain("\"type\":\"thread_intent_reported\"", $"session events: {sessionEvents}");

        cts.Cancel();
        try { await serverTask; } catch { }
    }

    private sealed class Factory : IAcpAgentFactory
    {
        public string RootDir { get; private set; } = string.Empty;

        public Task<InitializeResponse> InitializeAsync(InitializeRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new InitializeResponse
            {
                ProtocolVersion = request.ProtocolVersion,
                AgentInfo = new AgentInfo { AdditionalProperties = new Dictionary<string, object> { ["name"] = "harness-test-agent", ["version"] = "0" } },
                AgentCapabilities = new AgentCapabilities { PromptCapabilities = new PromptCapabilities(), LoadSession = false },
                AuthMethods = new List<AuthMethod>(),
            });

        public Task<NewSessionResponse> NewSessionAsync(NewSessionRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new NewSessionResponse { SessionId = "ses_thread_list", Modes = null, ConfigOptions = new List<SessionConfigOption>() });

        public IAcpSessionAgent CreateSessionAgent(string sessionId, IAcpClientCaller client, IAcpSessionEvents events)
        {
            RootDir = Path.Combine(Path.GetTempPath(), "harness-acp-thread-tests", Guid.NewGuid().ToString("N"));
            var store = new JsonlSessionStore(RootDir);
            store.CreateNew(sessionId, new SessionMetadata(
                SessionId: sessionId,
                Cwd: "/tmp",
                Title: "",
                CreatedAtIso: "2026-04-12T00:00:00Z",
                UpdatedAtIso: "2026-04-12T00:00:00Z"));

            var coreOptions = new CoreOptions { CommitAssistantTextDeltas = true };
            var publishOptions = new AcpPublishOptions(PublishReasoning: false);

            var chat = new ScriptedMeaiChatClient();

            var initialState = SessionState.Empty with
            {
                Tools = ImmutableArray.Create(
                    ToolSchemas.ReportIntent,
                    ToolSchemas.ThreadList,
                    ToolSchemas.ThreadNew,
                    ToolSchemas.ThreadFork,
                    ToolSchemas.ThreadSend,
                    ToolSchemas.ThreadRead),
            };

            return new HarnessAcpSessionAgent(sessionId, client, chat, events, coreOptions, publishOptions, store, initialState);
        }
    }

    private sealed class ScriptedMeaiChatClient : MeaiIChatClient
    {
        private int _calls;

        public IAsyncEnumerable<MeaiChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<MeaiChatMessage> messages, MeaiChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            _calls++;

            async IAsyncEnumerable<MeaiChatResponseUpdate> Step1()
            {
                // Required first tool call.
                // Tool call ids must be unique per model call.
                yield return new MeaiChatResponseUpdate
                {
                    Contents = new List<MeaiAIContent>
                    {
                        new MeaiFunctionCallContent($"call_{_calls}_0", "report_intent", new Dictionary<string, object?> { ["intent"] = "list threads" }),
                        new MeaiFunctionCallContent($"call_{_calls}_1", "thread_list", new Dictionary<string, object?>()),
                    }
                };
            }

            async IAsyncEnumerable<MeaiChatResponseUpdate> Step2()
            {
                yield return new MeaiChatResponseUpdate
                {
                    Contents = new List<MeaiAIContent> { new MeaiTextContent("Done.") },
                };
            }

            return _calls == 1 ? Step1() : Step2();
        }

        public Task<MeaiChatResponse> GetResponseAsync(IEnumerable<MeaiChatMessage> messages, MeaiChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new MeaiChatResponse(Array.Empty<MeaiChatMessage>()));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }

        public Task<string> CompleteAsync(IReadOnlyList<MeaiChatMessage> renderedMessages, CancellationToken cancellationToken)
            => Task.FromResult("");
    }
}
