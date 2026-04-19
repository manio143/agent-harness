using System.Text.Json;
using Agent.Acp.Acp;
using Agent.Acp.Schema;
using Agent.Harness.Acp;
using Agent.Harness.Persistence;
using Agent.Harness.Threads;
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

/// <summary>
/// Engine-seam migration of <see cref="AcpChildThreadIdleNotificationMainLogIntegrationTests"/>.
/// Asserts the parent idle notification is persisted in the main thread committed log with correct kind/meta.
/// </summary>
public sealed class EngineChildThreadIdleNotificationMainLogIntegrationTests
{
    [Fact]
    public async Task ChildBecomesIdle_ParentReceivesInboxNotification_PersistedInMainThreadLog()
    {
        var sessionId = "ses_child_idle";
        var root = Path.Combine(Path.GetTempPath(), "harness-engine-child-idle-mainlog-tests", Guid.NewGuid().ToString("N"));

        var store = new JsonlSessionStore(root);
        store.CreateNew(sessionId, new SessionMetadata(
            SessionId: sessionId,
            Cwd: "/tmp",
            Title: "",
            CreatedAtIso: "t0",
            UpdatedAtIso: "t1"));

        var threadStore = new JsonlThreadStore(root);
        var chat = new ScriptedMeaiChatClient();

        var agent = new HarnessAcpSessionAgent(
            sessionId,
            client: new AcpTwoPromptSameSessionLongLivedOrchestratorIntegrationTests.NullClientCaller(),
            chat: chat,
            chatByModel: _ => chat,
            quickWorkModel: "default",
            events: new AcpTwoPromptSameSessionLongLivedOrchestratorIntegrationTests.NullSessionEvents(),
            coreOptions: new Agent.Harness.CoreOptions { CommitAssistantTextDeltas = true },
            publishOptions: new Agent.Harness.Acp.AcpPublishOptions(PublishReasoning: false),
            store: store,
            initialState: Agent.Harness.SessionState.Empty);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var turn = new EngineChildThreadOrchestrationIntegrationTests.RecordingPromptTurn();

        // Prompt: model creates a child thread with immediate delivery. Child should run during drain.
        _ = await agent.PromptAsync(
            new PromptRequest
            {
                SessionId = sessionId,
                Prompt = new List<ContentBlock> { new TextContent { Text = "Hi" } },
            },
            turn,
            cts.Token);

        var childId = turn.CompletedRawOutputs
            .Where(x => x.ToolName == "thread_start")
            .Select(x => ExtractThreadId(x.RawOutput))
            .FirstOrDefault(x => x is not null);

        childId.Should().NotBeNull();
        childId!.Should().StartWith("thr_");

        var mainCommitted = threadStore.LoadCommittedEvents(sessionId, ThreadIds.Main);

        var hasIdle = mainCommitted.OfType<ThreadInboxMessageEnqueued>().Any(enq =>
            enq.ThreadId == ThreadIds.Main
            && enq.Kind == ThreadInboxMessageKind.ThreadIdleNotification
            && enq.SourceThreadId == childId
            && enq.Meta is not null
            && enq.Meta.TryGetValue(ThreadInboxMetaKeys.ChildThreadId, out var tid)
            && tid == childId);

        hasIdle.Should().BeTrue($"Expected a ThreadIdleNotification inbox enqueue in main log for child={childId}.\nMain committed:\n{string.Join("\n", mainCommitted.Select(e => e.ToString()))}");
    }

    private static string? ExtractThreadId(object? rawOutput)
    {
        if (rawOutput is null) return null;
        var json = JsonSerializer.Serialize(rawOutput);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("threadId", out var tid) && tid.ValueKind == JsonValueKind.String)
            return tid.GetString();
        return null;
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
                        new MeaiFunctionCallContent("call_m_1", "thread_start", new Dictionary<string, object?> { ["context"] = "fork", ["message"] = "do work", ["delivery"] = "immediate" }),
                    }
                };
                await Task.CompletedTask;
            }

            async IAsyncEnumerable<MeaiChatResponseUpdate> Main_Text_Done()
            {
                yield return new MeaiChatResponseUpdate
                {
                    Contents = new List<MeaiAIContent> { new MeaiTextContent("Created.") },
                };
                await Task.CompletedTask;
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
                await Task.CompletedTask;
            }

            async IAsyncEnumerable<MeaiChatResponseUpdate> Child_Text_Result()
            {
                yield return new MeaiChatResponseUpdate
                {
                    Contents = new List<MeaiAIContent> { new MeaiTextContent("ChildResult") },
                };
                await Task.CompletedTask;
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
    }
}
