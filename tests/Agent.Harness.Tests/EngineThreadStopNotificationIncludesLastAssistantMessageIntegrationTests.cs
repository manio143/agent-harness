using System.Text.Json;
using System.Text.RegularExpressions;
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
using MeaiFunctionResultContent = Microsoft.Extensions.AI.FunctionResultContent;
using MeaiTextContent = Microsoft.Extensions.AI.TextContent;

namespace Agent.Harness.Tests;

public sealed class EngineThreadStopNotificationIncludesLastAssistantMessageIntegrationTests
{
    [Fact]
    public async Task ThreadStop_EnqueuesParentNotification_WithLastAssistantMessageSnippet()
    {
        var sessionId = "ses_stop_last_msg";
        var root = Path.Combine(Path.GetTempPath(), "harness-engine-thread-stop-lastmsg-tests", Guid.NewGuid().ToString("N"));

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

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var turn1 = new EngineChildThreadOrchestrationIntegrationTests.RecordingPromptTurn();
        _ = await agent.PromptAsync(
            new PromptRequest
            {
                SessionId = sessionId,
                Prompt = new List<ContentBlock> { new TextContent { Text = "Hi" } },
            },
            turn1,
            cts.Token);

        var childId = turn1.CompletedRawOutputs
            .Where(x => x.ToolName == "thread_start")
            .Select(x => ExtractThreadId(x.RawOutput))
            .FirstOrDefault(x => x is not null);

        childId.Should().NotBeNullOrWhiteSpace();

        var turn2 = new EngineChildThreadOrchestrationIntegrationTests.RecordingPromptTurn();
        _ = await agent.PromptAsync(
            new PromptRequest
            {
                SessionId = sessionId,
                Prompt = new List<ContentBlock> { new TextContent { Text = "Stop child" } },
            },
            turn2,
            cts.Token);

        // The stop should enqueue a parent notification into the main thread committed log.
        var mainCommitted = threadStore.LoadCommittedEvents(sessionId, ThreadIds.Main);

        var stopEnq = mainCommitted
            .OfType<ThreadInboxMessageEnqueued>()
            .Where(e => e.Kind == ThreadInboxMessageKind.ThreadIdleNotification)
            .Where(e => e.SourceThreadId == childId)
            .LastOrDefault();

        stopEnq.Should().NotBeNull("expected a ThreadIdleNotification inbox enqueue in main log for stopped child={0}", childId);

        stopEnq!.Text.Should().Contain("Last assistant:");

        stopEnq.Meta.Should().NotBeNull();
        stopEnq.Meta!.TryGetValue(ThreadInboxMetaKeys.LastAssistantMessage, out var last).Should().BeTrue();
        last.Should().Contain("ChildResult");

        stopEnq.Meta!.TryGetValue(ThreadInboxMetaKeys.ClosedReason, out var reason).Should().BeTrue();
        reason.Should().Be("manual_stop");
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
        private bool _main1ToolsDone;
        private bool _main2ToolsDone;
        private bool _childDone;

        private static readonly Regex ChildIdFromIdleRe = new("<thread_idle child=\"([A-Za-z0-9_-]+)\"", RegexOptions.Compiled);

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
                    MeaiFunctionResultContent fr => JsonSerializer.Serialize(fr.Result, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                    _ => c.ToString() ?? string.Empty,
                }));
            }

            var rendered = string.Join("\n", messages.Select(Render));

            bool isChild = rendered.Contains("<thread_created", StringComparison.Ordinal) && rendered.Contains("<task>do work</task>", StringComparison.Ordinal);
            bool isMain1 = rendered.Contains("\nHi", StringComparison.Ordinal);
            bool isMain2 = rendered.Contains("\nStop child", StringComparison.Ordinal);

            async IAsyncEnumerable<MeaiChatResponseUpdate> Main1_Tools_CreateChild()
            {
                _main1ToolsDone = true;
                yield return new MeaiChatResponseUpdate
                {
                    Contents = new List<MeaiAIContent>
                    {
                        new MeaiFunctionCallContent("call_m1_0", "report_intent", new Dictionary<string, object?> { ["intent"] = "create child" }),
                        new MeaiFunctionCallContent("call_m1_1", "thread_start", new Dictionary<string, object?> { ["name"] = "child", ["context"] = "new", ["mode"] = "multi", ["message"] = "do work", ["delivery"] = "immediate" }),
                    }
                };
                await Task.CompletedTask;
            }

            async IAsyncEnumerable<MeaiChatResponseUpdate> Main1_Text_Done()
            {
                yield return new MeaiChatResponseUpdate { Contents = new List<MeaiAIContent> { new MeaiTextContent("Created") } };
                await Task.CompletedTask;
            }

            async IAsyncEnumerable<MeaiChatResponseUpdate> Child_Text_Run()
            {
                _childDone = true;
                yield return new MeaiChatResponseUpdate { Contents = new List<MeaiAIContent> { new MeaiTextContent("ChildResult") } };
                await Task.CompletedTask;
            }

            async IAsyncEnumerable<MeaiChatResponseUpdate> Main2_Tools_StopChild(string childId)
            {
                _main2ToolsDone = true;
                yield return new MeaiChatResponseUpdate
                {
                    Contents = new List<MeaiAIContent>
                    {
                        new MeaiFunctionCallContent("call_m2_0", "report_intent", new Dictionary<string, object?> { ["intent"] = "stop" }),
                        new MeaiFunctionCallContent("call_m2_1", "thread_stop", new Dictionary<string, object?> { ["threadId"] = childId, ["reason"] = "manual_stop" }),
                    }
                };
                await Task.CompletedTask;
            }

            async IAsyncEnumerable<MeaiChatResponseUpdate> Main2_Text_Done()
            {
                yield return new MeaiChatResponseUpdate { Contents = new List<MeaiAIContent> { new MeaiTextContent("Done") } };
                await Task.CompletedTask;
            }

            if (isChild)
                return !_childDone ? Child_Text_Run() : AsyncEmpty();

            if (isMain2)
            {
                var m = ChildIdFromIdleRe.Match(rendered);
                m.Success.Should().BeTrue("expected prompt history to include <thread_idle child=.../>; prompt was: {0}", rendered);
                return !_main2ToolsDone ? Main2_Tools_StopChild(m.Groups[1].Value) : Main2_Text_Done();
            }

            if (isMain1)
                return !_main1ToolsDone ? Main1_Tools_CreateChild() : Main1_Text_Done();

            // tolerate idle wakes
            if (rendered.Contains("<thread_idle", StringComparison.Ordinal))
                return Main1_Text_Done();

            throw new InvalidOperationException($"Unexpected prompt. Rendered=\n{rendered}");
        }

        private static async IAsyncEnumerable<MeaiChatResponseUpdate> AsyncEmpty()
        {
            yield break;
        }

        public Task<MeaiChatResponse> GetResponseAsync(IEnumerable<MeaiChatMessage> messages, MeaiChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new MeaiChatResponse(Array.Empty<MeaiChatMessage>()));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
