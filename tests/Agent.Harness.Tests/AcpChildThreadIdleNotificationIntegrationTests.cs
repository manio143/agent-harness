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

public sealed class AcpChildThreadIdleNotificationIntegrationTests
{
    [Fact]
    public async Task ChildIdleNotification_IsEnqueuedOnlyAfterChildConsumesPendingEnqueue()
    {
        var (clientTransport, serverTransport) = InMemoryTransport.CreatePair();

        var factory = new Factory();
        var server = new AcpAgentServer(factory);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
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

        // Prompt 1: create child + enqueue follow-up message.
        _ = await client.RequestAsync<PromptRequest, PromptResponse>(
            "session/prompt",
            new PromptRequest
            {
                SessionId = newSession.SessionId,
                Prompt = new List<ContentBlock> { new TextContent { Text = "Hi" } },
            },
            cts.Token);

        // Give any fire-and-forget ACP updates time to arrive.
        await Task.Delay(200, cts.Token);

        // Extract custom updates.
        var enqueued = new List<JsonElement>();
        foreach (var n in updates)
        {
            var raw = n.Params?.GetRawText() ?? "{}";
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("update", out var upd)) continue;
            if (!upd.TryGetProperty("kind", out var kindEl)) continue;
            if (kindEl.GetString() != "thread_inbox_message_enqueued") continue;
            enqueued.Add(upd.Clone());
        }

        enqueued.Should().NotBeEmpty();

        var texts = enqueued.Select(e => e.GetProperty("text").GetString() ?? "").ToList();
        texts.Should().Contain(t => t.Contains("enqueue followup", StringComparison.Ordinal), $"enqueued texts were: {string.Join(" | ", texts)}");
        texts.Should().Contain(t => t.Contains("became idle", StringComparison.Ordinal), $"enqueued texts were: {string.Join(" | ", texts)}");

        var followup = enqueued.Single(e => (e.GetProperty("text").GetString() ?? "").Contains("enqueue followup", StringComparison.Ordinal));
        var idle = enqueued.Single(e => (e.GetProperty("text").GetString() ?? "").Contains("became idle", StringComparison.Ordinal));

        DateTimeOffset.Parse(followup.GetProperty("enqueuedAtIso").GetString()!).Should().BeBefore(DateTimeOffset.Parse(idle.GetProperty("enqueuedAtIso").GetString()!));

        cts.Cancel();
        try { await serverTask; } catch { }
    }

    private sealed class Factory : IAcpAgentFactory
    {
        public ScriptedMeaiChatClient Chat { get; } = new();

        public Task<InitializeResponse> InitializeAsync(InitializeRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new InitializeResponse
            {
                ProtocolVersion = request.ProtocolVersion,
                AgentInfo = new AgentInfo { AdditionalProperties = new Dictionary<string, object> { ["name"] = "harness-test-agent", ["version"] = "0" } },
                AgentCapabilities = new AgentCapabilities { PromptCapabilities = new PromptCapabilities(), LoadSession = false },
                AuthMethods = new List<AuthMethod>(),
            });

        public Task<NewSessionResponse> NewSessionAsync(NewSessionRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new NewSessionResponse { SessionId = "ses_child_idle_notify", Modes = null, ConfigOptions = new List<SessionConfigOption>() });

        public IAcpSessionAgent CreateSessionAgent(string sessionId, IAcpClientCaller client, IAcpSessionEvents events)
        {
            var rootDir = Path.Combine(Path.GetTempPath(), "harness-acp-child-idle-notify-tests", Guid.NewGuid().ToString("N"));
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
        private bool _mainHiToolsDone;
        private bool _mainHiSendDone;
        private bool _mainHiTextDone;

        private static readonly Regex ChildIdRe = new("thr_[a-f0-9]{12,}", RegexOptions.Compiled | RegexOptions.IgnoreCase);

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

            var rendered = string.Join("\n", messages.Select(Render));

            bool isChild = rendered.Contains("<inbox>", StringComparison.Ordinal);

            async IAsyncEnumerable<MeaiChatResponseUpdate> MainHi_Tools_CreateChild()
            {
                _mainHiToolsDone = true;
                yield return new MeaiChatResponseUpdate
                {
                    Contents = new List<MeaiAIContent>
                    {
                        new MeaiFunctionCallContent("call_mhi_0", "report_intent", new Dictionary<string, object?> { ["intent"] = "create child" }),
                        new MeaiFunctionCallContent("call_mhi_1", "thread_new", new Dictionary<string, object?> { ["message"] = "do work", ["delivery"] = "immediate" }),
                    }
                };
            }

            async IAsyncEnumerable<MeaiChatResponseUpdate> MainHi_Tools_SendEnqueueFollowup()
            {
                _mainHiSendDone = true;

                var match = ChildIdRe.Match(rendered);
                if (!match.Success)
                    throw new InvalidOperationException($"Expected child id in rendered prompt, got: {rendered}");

                var childId = match.Value;

                yield return new MeaiChatResponseUpdate
                {
                    Contents = new List<MeaiAIContent>
                    {
                        new MeaiFunctionCallContent("call_mhi2_0", "report_intent", new Dictionary<string, object?> { ["intent"] = "send followup" }),
                        new MeaiFunctionCallContent("call_mhi2_1", "thread_send", new Dictionary<string, object?> { ["threadId"] = childId, ["message"] = "enqueue followup", ["delivery"] = "enqueue" }),
                    }
                };
            }

            async IAsyncEnumerable<MeaiChatResponseUpdate> MainHi_Text()
            {
                _mainHiTextDone = true;
                yield return new MeaiChatResponseUpdate
                {
                    Contents = new List<MeaiAIContent> { new MeaiTextContent("Ok") },
                };
            }

            async IAsyncEnumerable<MeaiChatResponseUpdate> Child_Text()
            {
                yield return new MeaiChatResponseUpdate
                {
                    Contents = new List<MeaiAIContent> { new MeaiTextContent("ChildRun") },
                };
            }

            if (isChild)
                return Child_Text();

            if (!_mainHiToolsDone) return MainHi_Tools_CreateChild();
            if (!_mainHiSendDone) return MainHi_Tools_SendEnqueueFollowup();
            if (!_mainHiTextDone) return MainHi_Text();
            return AsyncEmpty();
        }

        private static async IAsyncEnumerable<MeaiChatResponseUpdate> AsyncEmpty()
        {
            yield break;
        }

        public Task<MeaiChatResponse> GetResponseAsync(IEnumerable<MeaiChatMessage> messages, MeaiChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new MeaiChatResponse(Array.Empty<MeaiChatMessage>()));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }

        public Task<string> CompleteAsync(IReadOnlyList<MeaiChatMessage> renderedMessages, CancellationToken cancellationToken)
            => Task.FromResult("");
    }
}
