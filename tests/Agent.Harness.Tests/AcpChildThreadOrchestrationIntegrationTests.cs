using System.Text.Json;
using System.Text.RegularExpressions;
using Agent.Acp.Acp;
using Agent.Acp.Schema;
using Agent.Acp.Tests;
using Agent.Acp.Protocol;
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

public sealed class AcpChildThreadOrchestrationIntegrationTests
{
    [Fact(Skip = "Migrated to EngineChildThreadOrchestrationIntegrationTests (engine seam, no JSON-RPC transport)")]
    public async Task ThreadNew_Immediate_RunsChild_AndThreadReadReturnsChildAssistantMessages()
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

        // Prompt 1: model creates a child thread with immediate delivery. Child should run via scheduler.
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

        // Sanity: child event log should exist after orchestration run.
        factory.RootDir.Should().NotBeNull();
        var childEventsPath = Path.Combine(factory.RootDir!, newSession.SessionId, "threads", childId!, "events.jsonl");

        // Prompt 2: ask the agent to read the child thread.
        _ = await client.RequestAsync<PromptRequest, PromptResponse>(
            "session/prompt",
            new PromptRequest
            {
                SessionId = newSession.SessionId,
                Prompt = new List<ContentBlock> { new TextContent { Text = $"Check child={childId}" } },
            },
            cts.Token);

        // Wait until we observe a tool call update with a { messages: [...] } payload (thread_read).
        JsonElement? threadReadResult = null;
        for (var attempt = 0; attempt < 20 && threadReadResult is null; attempt++)
        {
            foreach (var n in updates)
            {
                var payload = JsonSerializer.Deserialize<SessionNotification>(n.Params?.GetRawText() ?? "{}", AcpJson.Options);
                if (payload?.Update is not Agent.Acp.Schema.ToolCallUpdate tc) continue;
                if (tc.Status.Value != ToolCallStatus.Completed) continue;
                if (tc.RawOutput is null) continue;

                var raw = JsonSerializer.Serialize(tc.RawOutput, AcpJson.Options);
                using var doc = JsonDocument.Parse(raw);
                if (!doc.RootElement.TryGetProperty("messages", out _)) continue;

                threadReadResult = doc.RootElement.Clone();
                break;
            }

            if (threadReadResult is null)
                await Task.Delay(50, cts.Token);
        }

        // Ensure we actually wrote something for the child.
        File.Exists(childEventsPath).Should().BeTrue($"expected child events at {childEventsPath}");
        var childEvents = await File.ReadAllTextAsync(childEventsPath, cts.Token);
        childEvents.Should().Contain("ChildResult", $"child events were: {childEvents}");

        threadReadResult.Should().NotBeNull($"Rendered second prompt was: {factory.Chat.LastSecondPromptRenderedText}. Child events: {childEvents}");

        var messages = threadReadResult!.Value.GetProperty("messages");
        messages.ValueKind.Should().Be(JsonValueKind.Array);

        static string? GetText(JsonElement el)
        {
            if (el.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String) return t.GetString();
            if (el.TryGetProperty("Text", out var T) && T.ValueKind == JsonValueKind.String) return T.GetString();
            return null;
        }

        messages.EnumerateArray().Select(GetText).Should().Contain("ChildResult");

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
            => Task.FromResult(new NewSessionResponse { SessionId = "ses_child_orch", Modes = null, ConfigOptions = new List<SessionConfigOption>() });

        public IAcpSessionAgent CreateSessionAgent(string sessionId, IAcpClientCaller client, IAcpSessionEvents events)
        {
            var rootDir = Path.Combine(Path.GetTempPath(), "harness-acp-child-orch-tests", Guid.NewGuid().ToString("N"));
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

            return new HarnessAcpSessionAgent(sessionId, client, Chat, events, coreOptions, publishOptions, store, initialState);
        }
    }

    private sealed class ScriptedMeaiChatClient : MeaiIChatClient
    {
        private bool _main1ToolsDone;
        private bool _main2ToolsDone;
        private bool _childToolsDone;

        private static readonly Regex ChildIdRe = new("thr_[a-f0-9]{12}", RegexOptions.Compiled);

        public string? LastSecondPromptRenderedText { get; private set; }

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

            bool isMainPrompt2 = msgText.Contains("Check child=", StringComparison.Ordinal);
            bool isChildPrompt = msgText.Contains("<inter_thread", StringComparison.Ordinal) && msgText.Contains("do work", StringComparison.Ordinal);

            // After child becomes idle, the parent may receive a <thread_idle .../> system message and
            // be woken automatically (wake is an effect). Treat this as a follow-up main prompt.
            bool isMainPromptIdle = msgText.Contains("<thread_idle", StringComparison.Ordinal);

            bool isMainPrompt1 = !isMainPrompt2 && !isMainPromptIdle && msgText.Contains("\nHi", StringComparison.Ordinal);

            async IAsyncEnumerable<MeaiChatResponseUpdate> Main1_Tools_CreateChild()
            {
                _main1ToolsDone = true;
                yield return new MeaiChatResponseUpdate
                {
                    Contents = new List<MeaiAIContent>
                    {
                        new MeaiFunctionCallContent("call_m1_0", "report_intent", new Dictionary<string, object?> { ["intent"] = "create child" }),
                        new MeaiFunctionCallContent("call_m1_1", "thread_new", new Dictionary<string, object?> { ["message"] = "do work", ["delivery"] = "immediate" }),
                    }
                };
            }

            async IAsyncEnumerable<MeaiChatResponseUpdate> Main1_Text_Done()
            {
                yield return new MeaiChatResponseUpdate
                {
                    Contents = new List<MeaiAIContent> { new MeaiTextContent("Created.") },
                };
            }

            async IAsyncEnumerable<MeaiChatResponseUpdate> Child1_Tools_ListThreads()
            {
                _childToolsDone = true;
                yield return new MeaiChatResponseUpdate
                {
                    Contents = new List<MeaiAIContent>
                    {
                        new MeaiFunctionCallContent("call_c_0", "report_intent", new Dictionary<string, object?> { ["intent"] = "child work" }),
                        new MeaiFunctionCallContent("call_c_1", "thread_list", new Dictionary<string, object?>()),
                    }
                };
            }

            async IAsyncEnumerable<MeaiChatResponseUpdate> Child2_Text_Result()
            {
                yield return new MeaiChatResponseUpdate
                {
                    Contents = new List<MeaiAIContent> { new MeaiTextContent("ChildResult") },
                };
            }

            async IAsyncEnumerable<MeaiChatResponseUpdate> Main2_Tools_ReadChild()
            {
                _main2ToolsDone = true;
                LastSecondPromptRenderedText = msgText;

                var match = ChildIdRe.Match(msgText);
                if (!match.Success)
                    throw new InvalidOperationException($"Expected child id in messages, got: {msgText}");

                var childId = match.Value;
                yield return new MeaiChatResponseUpdate
                {
                    Contents = new List<MeaiAIContent>
                    {
                        new MeaiFunctionCallContent("call_m2_0", "report_intent", new Dictionary<string, object?> { ["intent"] = "read child" }),
                        new MeaiFunctionCallContent("call_m2_1", "thread_read", new Dictionary<string, object?> { ["threadId"] = childId }),
                    }
                };
            }

            async IAsyncEnumerable<MeaiChatResponseUpdate> Main2_Text_Done()
            {
                yield return new MeaiChatResponseUpdate
                {
                    Contents = new List<MeaiAIContent> { new MeaiTextContent("Checked.") },
                };
            }

            if (isMainPrompt2)
                return !_main2ToolsDone ? Main2_Tools_ReadChild() : Main2_Text_Done();

            if (isChildPrompt)
                return !_childToolsDone ? Child1_Tools_ListThreads() : Child2_Text_Result();

            if (isMainPrompt1)
                return !_main1ToolsDone ? Main1_Tools_CreateChild() : Main1_Text_Done();

            if (isMainPromptIdle)
                return Main1_Text_Done();

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
