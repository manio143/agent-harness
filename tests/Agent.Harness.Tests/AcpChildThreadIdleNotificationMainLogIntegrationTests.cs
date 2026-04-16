using System.Text.Json;
using Agent.Acp.Acp;
using Agent.Acp.Schema;
using Agent.Acp.Tests;
using Agent.Acp.Protocol;
using Agent.Harness.Acp;
using Agent.Harness.Persistence;
using Agent.Harness.Threads;
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

public sealed class AcpChildThreadIdleNotificationMainLogIntegrationTests
{
    [Fact(Skip = "Migrated to EngineChildThreadIdleNotificationMainLogIntegrationTests (engine seam, no JSON-RPC transport)")]
    public async Task ChildBecomesIdle_ParentReceivesInboxNotification_PersistedInMainThreadLog()
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();

        var factory = new Factory();
        var server = new AcpAgentServer(factory);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serverTask = Task.Run(() => server.RunAsync(serverTransport, cts.Token), cts.Token);

        await using var client = new AcpClientConnection(clientTransport);

        var updates = new List<JsonRpcNotification>();
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

        // Prompt 1: model creates a child thread with immediate delivery.
        _ = await client.RequestAsync<PromptRequest, PromptResponse>(
            "session/prompt",
            new PromptRequest
            {
                SessionId = newSession.SessionId,
                Prompt = new List<ContentBlock> { new TextContent { Text = "Hi" } },
            },
            cts.Token);

        // Wait for the child orchestration background task.
        await Task.Delay(800, cts.Token);

        // Extract child id from thread_new rawOutput.
        string? childId = null;
        foreach (var n in updates)
        {
            var payload = JsonSerializer.Deserialize<SessionNotification>(n.Params?.GetRawText() ?? "{}", AcpJson.Options);
            if (payload?.Update is not Agent.Acp.Schema.ToolCallUpdate tc) continue;
            if (tc.Status.Value != ToolCallStatus.Completed) continue;
            if (tc.RawOutput is null) continue;

            var raw = JsonSerializer.Serialize(tc.RawOutput, AcpJson.Options);
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("threadId", out var tid) && tid.ValueKind == JsonValueKind.String)
            {
                childId = tid.GetString();
                break;
            }
        }

        childId.Should().NotBeNull();
        childId!.Should().StartWith("thr_");

        // Assert: main committed log contains a child-idle notification inbox enqueue.
        factory.RootDir.Should().NotBeNull();
        var threadStore = new JsonlThreadStore(factory.RootDir!);

        var mainCommitted = threadStore.LoadCommittedEvents(newSession.SessionId, ThreadIds.Main);

        var hasIdle = mainCommitted.OfType<ThreadInboxMessageEnqueued>().Any(enq =>
            enq.ThreadId == ThreadIds.Main
            && enq.Kind == ThreadInboxMessageKind.ThreadIdleNotification
            && enq.SourceThreadId == childId
            && enq.Meta is not null
            && enq.Meta.TryGetValue("childThreadId", out var tid)
            && tid == childId);

        Assert.True(hasIdle, $"Expected a ThreadIdleNotification inbox enqueue in main log for child={childId}.\nMain committed:\n{string.Join("\n", mainCommitted.Select(e => e.ToString()))}");

        cts.Cancel();
        try { await serverTask; } catch { }
    }

    private sealed class Factory : IAcpAgentFactory
    {
        public ScriptedMeaiChatClient Chat { get; } = new();
        public string? RootDir { get; private set; }

        public Task<InitializeResponse> InitializeAsync(InitializeRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new InitializeResponse
            {
                ProtocolVersion = request.ProtocolVersion,
                AgentInfo = new AgentInfo { AdditionalProperties = new Dictionary<string, object> { ["name"] = "harness-test-agent", ["version"] = "0" } },
                AgentCapabilities = new AgentCapabilities { PromptCapabilities = new PromptCapabilities(), LoadSession = false },
                AuthMethods = new List<AuthMethod>(),
            });

        public Task<NewSessionResponse> NewSessionAsync(NewSessionRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new NewSessionResponse { SessionId = "ses_child_idle", Modes = null, ConfigOptions = new List<SessionConfigOption>() });

        public IAcpSessionAgent CreateSessionAgent(string sessionId, IAcpClientCaller client, IAcpSessionEvents events)
        {
            var rootDir = Path.Combine(Path.GetTempPath(), "harness-acp-child-idle-tests", Guid.NewGuid().ToString("N"));
            RootDir = rootDir;
            var store = new JsonlSessionStore(rootDir);
            store.CreateNew(sessionId, new SessionMetadata(
                SessionId: sessionId,
                Cwd: "/tmp",
                Title: "",
                CreatedAtIso: "2026-04-12T00:00:00Z",
                UpdatedAtIso: "2026-04-12T00:00:00Z"));

            var coreOptions = new CoreOptions { CommitAssistantTextDeltas = true };
            var publishOptions = new AcpPublishOptions(PublishReasoning: false);

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

            return new Agent.Harness.Acp.HarnessAcpSessionAgent(sessionId, client, Chat, events, coreOptions, publishOptions, store, initialState);
        }
    }

    private sealed class ScriptedMeaiChatClient : MeaiIChatClient
    {
        private bool _mainToolsDone;
        private bool _childToolsDone;

        public IAsyncEnumerable<MeaiChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<MeaiChatMessage> messages, MeaiChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            static string Render(MeaiChatMessage m)
            {
                if (!string.IsNullOrEmpty(m.Text)) return m.Text!;
                if (m.Contents is null) return string.Empty;

                return string.Join("\n", m.Contents.Select(c => c switch
                {
                    MeaiTextContent t => t.Text ?? string.Empty,
                    MeaiFunctionCallContent fc => $"<tool_call name=\"{fc.Name}\"/>",
                    _ => c.ToString() ?? string.Empty,
                }));
            }

            var msgText = string.Join("\n", messages.Select(Render));

            bool isChildPrompt = msgText.Contains("<inter_thread", StringComparison.Ordinal) && msgText.Contains("do work", StringComparison.Ordinal);
            bool isMainPrompt = !isChildPrompt && msgText.Contains("\nHi", StringComparison.Ordinal);

            async IAsyncEnumerable<MeaiChatResponseUpdate> Main_Tools_CreateChild()
            {
                _mainToolsDone = true;
                yield return new MeaiChatResponseUpdate
                {
                    Contents = new List<MeaiAIContent>
                    {
                        new MeaiFunctionCallContent("call_m_0", "report_intent", new Dictionary<string, object?> { ["intent"] = "create child" }),
                        new MeaiFunctionCallContent("call_m_1", "thread_new", new Dictionary<string, object?> { ["message"] = "do work", ["delivery"] = "immediate" }),
                    }
                };
            }

            async IAsyncEnumerable<MeaiChatResponseUpdate> Main_Text_Done()
            {
                yield return new MeaiChatResponseUpdate
                {
                    Contents = new List<MeaiAIContent> { new MeaiTextContent("Created.") },
                };
            }

            async IAsyncEnumerable<MeaiChatResponseUpdate> Child_Tools_ReportIntent()
            {
                _childToolsDone = true;
                yield return new MeaiChatResponseUpdate
                {
                    Contents = new List<MeaiAIContent>
                    {
                        new MeaiFunctionCallContent("call_c_0", "report_intent", new Dictionary<string, object?> { ["intent"] = "child work" }),
                    }
                };
            }

            async IAsyncEnumerable<MeaiChatResponseUpdate> Child_Text_Result()
            {
                yield return new MeaiChatResponseUpdate
                {
                    Contents = new List<MeaiAIContent> { new MeaiTextContent("ChildResult") },
                };
            }

            if (isChildPrompt)
                return !_childToolsDone ? Child_Tools_ReportIntent() : Child_Text_Result();

            if (isMainPrompt)
                return !_mainToolsDone ? Main_Tools_CreateChild() : Main_Text_Done();

            throw new InvalidOperationException($"Unexpected prompt messages: {msgText}");
        }

        public Task<MeaiChatResponse> GetResponseAsync(IEnumerable<MeaiChatMessage> messages, MeaiChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new MeaiChatResponse(Array.Empty<MeaiChatMessage>()));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }

        public Task<string> CompleteAsync(IReadOnlyList<MeaiChatMessage> renderedMessages, CancellationToken cancellationToken)
            => Task.FromResult("");
    }
}
