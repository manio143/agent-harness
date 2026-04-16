using System.Collections.Immutable;
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

public sealed class ThreadOrchestratorEndToEndEnqueueGatingIntegrationTests
{
    [Fact]
    public async Task ChildCanEnqueueToParent_AndParentProcessesOnlyWhenIdle()
    {
        var sessionId = "s1";
        var root = Path.Combine(Path.GetTempPath(), "harness-thread-orch-e2e", Guid.NewGuid().ToString("N"));
        var sessionStore = new JsonlSessionStore(root);
        sessionStore.CreateNew(sessionId, new SessionMetadata(
            SessionId: sessionId,
            Cwd: "/tmp",
            Title: "",
            CreatedAtIso: "t0",
            UpdatedAtIso: "t1"));

        var threadStore = new InMemoryThreadStore();
        var threads = new ThreadManager(sessionId, threadStore);

        var chat = new ScriptedChat();
        var coreOptions = new CoreOptions { CommitAssistantTextDeltas = true };

        var orchestrator = new ThreadOrchestrator(
            sessionId,
            client: new FakeCaller(),
            chat: chat,
            mcp: NullMcpToolInvoker.Instance,
            coreOptions: coreOptions,
            logLlmPrompts: false,
            sessionStore: sessionStore,
            threadStore: threadStore,
            threads: threads);

        // User prompt enters main inbox (immediate). Then schedule main.
        await orchestrator.ObserveAsync(ThreadIds.Main, new ObservedInboxMessageArrived(
            ThreadId: ThreadIds.Main,
            Kind: ThreadInboxMessageKind.UserPrompt,
            Delivery: InboxDelivery.Immediate,
            EnvelopeId: ThreadEnvelopes.NewEnvelopeId(),
            EnqueuedAtIso: "t0",
            Source: "user",
            SourceThreadId: null,
            Text: "Hi",
            Meta: null), CancellationToken.None);

        orchestrator.ScheduleRun(ThreadIds.Main);
        await orchestrator.RunUntilQuiescentAsync(CancellationToken.None);

        // Child enqueued a message to the parent. With event-driven waking (wake is an effect),
        // the parent should be automatically woken once it reaches an idle boundary, and the
        // enqueue should be promoted/dequeued during the same drain-to-quiescence.
        var mainCommitted = threadStore.LoadCommittedEvents(sessionId, ThreadIds.Main);
        mainCommitted.OfType<ThreadInboxMessageEnqueued>().Any(e => e.Text == "from child").Should().BeTrue();
        mainCommitted.OfType<InterThreadMessage>().Any(m => m.Text == "from child").Should().BeTrue();

        // Ensure it was gated by enqueue semantics: we should see an enqueue+dequeue pair for that message.
        var enq = mainCommitted.OfType<ThreadInboxMessageEnqueued>().Single(e => e.Text == "from child");
        mainCommitted.OfType<ThreadInboxMessageDequeued>().Any(d => d.EnvelopeId == enq.EnvelopeId).Should().BeTrue();

        // Assert call ordering: main prompt at least once, child prompt at least twice (tools then text).
        chat.MainPromptCount.Should().BeGreaterThanOrEqualTo(1);
        chat.ChildPromptCount.Should().BeGreaterOrEqualTo(2);
    }

    private sealed class FakeCaller : IAcpClientCaller
    {
        public ClientCapabilities ClientCapabilities { get; } = new() { Fs = new FileSystemCapabilities() };

        public Task<TResponse> RequestAsync<TRequest, TResponse>(string method, TRequest request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
    }

    private sealed class ScriptedChat : MeaiIChatClient
    {
        private bool _main1ToolsDone;
        private bool _main2ToolsDone;
        private bool _childToolsDone;

        private static readonly Regex ChildIdRe = new("thr_[a-f0-9]{12}", RegexOptions.Compiled);

        public int MainPromptCount { get; private set; }
        public int ChildPromptCount { get; private set; }

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

            var isChildPrompt = msgText.Contains("<inter_thread", StringComparison.Ordinal);
            var isMainPrompt2 = msgText.Contains("from child", StringComparison.Ordinal);
            var isMainPrompt1 = !isChildPrompt && !isMainPrompt2 && msgText.Contains("\nHi", StringComparison.Ordinal);

            if (isMainPrompt1)
            {
                MainPromptCount++;
                return !_main1ToolsDone ? Main1_Tools_CreateChild() : Main1_Text_Done();
            }

            if (isChildPrompt)
            {
                ChildPromptCount++;
                return !_childToolsDone ? Child1_Tools_EnqueueToParent() : Child2_Text_Done();
            }

            if (isMainPrompt2)
            {
                MainPromptCount++;
                return !_main2ToolsDone ? Main2_Tools_ListThreads() : Main2_Text_Done();
            }

            throw new InvalidOperationException($"Unexpected prompt messages: {msgText}");

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
                    Contents = new List<MeaiAIContent> { new MeaiTextContent("MainDone") },
                };
            }

            async IAsyncEnumerable<MeaiChatResponseUpdate> Child1_Tools_EnqueueToParent()
            {
                _childToolsDone = true;
                // Determine the parent thread id is 'main'; just enqueue to main.
                yield return new MeaiChatResponseUpdate
                {
                    Contents = new List<MeaiAIContent>
                    {
                        new MeaiFunctionCallContent("call_c_0", "report_intent", new Dictionary<string, object?> { ["intent"] = "child sends" }),
                        new MeaiFunctionCallContent("call_c_1", "thread_send", new Dictionary<string, object?> { ["threadId"] = "main", ["message"] = "from child", ["delivery"] = "enqueue" }),
                    }
                };
            }

            async IAsyncEnumerable<MeaiChatResponseUpdate> Child2_Text_Done()
            {
                yield return new MeaiChatResponseUpdate
                {
                    Contents = new List<MeaiAIContent> { new MeaiTextContent("ChildDone") },
                };
            }

            async IAsyncEnumerable<MeaiChatResponseUpdate> Main2_Tools_ListThreads()
            {
                _main2ToolsDone = true;

                // Assert we can still parse the child id from the prompt (optional sanity).
                var match = ChildIdRe.Match(msgText);
                match.Success.Should().BeTrue();

                yield return new MeaiChatResponseUpdate
                {
                    Contents = new List<MeaiAIContent>
                    {
                        new MeaiFunctionCallContent("call_m2_0", "report_intent", new Dictionary<string, object?> { ["intent"] = "process child" }),
                        new MeaiFunctionCallContent("call_m2_1", "thread_list", new Dictionary<string, object?>()),
                    }
                };
            }

            async IAsyncEnumerable<MeaiChatResponseUpdate> Main2_Text_Done()
            {
                yield return new MeaiChatResponseUpdate
                {
                    Contents = new List<MeaiAIContent> { new MeaiTextContent("MainFollowup") },
                };
            }
        }

        public Task<MeaiChatResponse> GetResponseAsync(IEnumerable<MeaiChatMessage> messages, MeaiChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new MeaiChatResponse(Array.Empty<MeaiChatMessage>()));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }

        public Task<string> CompleteAsync(IReadOnlyList<MeaiChatMessage> renderedMessages, CancellationToken cancellationToken)
            => Task.FromResult("");
    }
}
