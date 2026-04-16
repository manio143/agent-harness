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
using MeaiTextContent = Microsoft.Extensions.AI.TextContent;

namespace Agent.Harness.Tests;

/// <summary>
/// Engine-seam migration of <see cref="AcpChildThreadIdleNotificationIntegrationTests"/>.
/// Validates the causal ordering: child follow-up enqueue happens before the parent idle notification enqueue.
/// </summary>
public sealed class EngineChildThreadIdleNotificationIntegrationTests
{
    [Fact]
    public async Task ChildIdleNotification_IsEnqueuedOnlyAfterChildConsumesPendingEnqueue()
    {
        var sessionId = "ses_child_idle_notify";
        var root = Path.Combine(Path.GetTempPath(), "harness-engine-child-idle-notify-tests", Guid.NewGuid().ToString("N"));

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
            chat,
            events: new AcpTwoPromptSameSessionLongLivedOrchestratorIntegrationTests.NullSessionEvents(),
            coreOptions: new Agent.Harness.CoreOptions { CommitAssistantTextDeltas = true },
            publishOptions: new Agent.Harness.Acp.AcpPublishOptions(PublishReasoning: false),
            store,
            initialState: Agent.Harness.SessionState.Empty);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Prompt 1: create child + enqueue follow-up message.
        _ = await agent.PromptAsync(
            new PromptRequest
            {
                SessionId = sessionId,
                Prompt = new List<ContentBlock> { new TextContent { Text = "Hi" } },
            },
            new AcpTwoPromptSameSessionLongLivedOrchestratorIntegrationTests.NullPromptTurn(),
            cts.Token);

        chat.LastChildThreadId.Should().NotBeNull();
        var childId = chat.LastChildThreadId!;

        // Followup was enqueued to the child thread.
        var childEvts = threadStore.LoadCommittedEvents(sessionId, childId);
        var followup = childEvts
            .OfType<ThreadInboxMessageEnqueued>()
            .Single(e => e.Text.Contains("enqueue followup", StringComparison.Ordinal));

        // Idle notification was enqueued to the parent (main) via the parent thread committed log.
        var parentEvts = threadStore.LoadCommittedEvents(sessionId, ThreadIds.Main);
        var idle = parentEvts
            .OfType<ThreadInboxMessageEnqueued>()
            .Single(e => e.Text.Contains("became idle", StringComparison.Ordinal));

        DateTimeOffset.Parse(followup.EnqueuedAtIso).Should().BeBefore(DateTimeOffset.Parse(idle.EnqueuedAtIso));
    }

    private sealed class ScriptedMeaiChatClient : MeaiIChatClient
    {
        private bool _mainHiToolsDone;
        private bool _mainHiSendDone;
        private bool _mainHiTextDone;

        public string? LastChildThreadId { get; private set; }

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
                await Task.CompletedTask;
            }

            async IAsyncEnumerable<MeaiChatResponseUpdate> MainHi_Tools_SendEnqueueFollowup()
            {
                _mainHiSendDone = true;

                var match = ChildIdRe.Match(rendered);
                if (!match.Success)
                    throw new InvalidOperationException($"Expected child id in rendered prompt, got: {rendered}");

                var childId = match.Value;
                LastChildThreadId = childId;

                yield return new MeaiChatResponseUpdate
                {
                    Contents = new List<MeaiAIContent>
                    {
                        new MeaiFunctionCallContent("call_mhi2_0", "report_intent", new Dictionary<string, object?> { ["intent"] = "send followup" }),
                        new MeaiFunctionCallContent("call_mhi2_1", "thread_send", new Dictionary<string, object?> { ["threadId"] = childId, ["message"] = "enqueue followup", ["delivery"] = "enqueue" }),
                    }
                };
                await Task.CompletedTask;
            }

            async IAsyncEnumerable<MeaiChatResponseUpdate> MainHi_Text()
            {
                _mainHiTextDone = true;
                yield return new MeaiChatResponseUpdate
                {
                    Contents = new List<MeaiAIContent> { new MeaiTextContent("Ok") },
                };
                await Task.CompletedTask;
            }

            async IAsyncEnumerable<MeaiChatResponseUpdate> Child_Text()
            {
                yield return new MeaiChatResponseUpdate
                {
                    Contents = new List<MeaiAIContent> { new MeaiTextContent("ChildRun") },
                };
                await Task.CompletedTask;
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
    }
}
