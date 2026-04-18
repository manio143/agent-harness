using System.Collections.Immutable;
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

public sealed class ThreadOrchestratorObserveConcurrencyTests
{
    [Fact]
    public async Task Observe_IsThreadGated_WithRunSoStateDoesNotClobber()
    {
        var sessionId = "s1";
        var root = Path.Combine(Path.GetTempPath(), "harness-orch-observe-concurrency", Guid.NewGuid().ToString("N"));
        var sessionStore = new JsonlSessionStore(root);
        sessionStore.CreateNew(sessionId, new SessionMetadata(sessionId, "/tmp", Title: "", CreatedAtIso: "t0", UpdatedAtIso: "t1"));

        var threadStore = new InMemoryThreadStore();
        var threads = new ThreadManager(sessionId, threadStore);

        var chat = new OneShotChat();
        var coreOptions = new CoreOptions { CommitAssistantTextDeltas = true };

        var orch = new ThreadOrchestrator(
            sessionId,
            client: new FakeCaller(),
            chat: chat,
            mcp: NullMcpToolInvoker.Instance,
            coreOptions: coreOptions,
            logLlmPrompts: false,
            sessionStore: sessionStore,
            threadStore: threadStore,
            threads: threads);

        orch.InitializeToolCatalog(ImmutableArray.Create(
            ToolSchemas.ReportIntent,
            ToolSchemas.ThreadList,
            ToolSchemas.ThreadNew,
            ToolSchemas.ThreadFork,
            ToolSchemas.ThreadSend,
            ToolSchemas.ThreadRead));

        // Seed: a message that will cause a model call.
        await orch.ObserveAsync(ThreadIds.Main, new ObservedInboxMessageArrived(
            ThreadId: ThreadIds.Main,
            Kind: ThreadInboxMessageKind.UserPrompt,
            Delivery: InboxDelivery.Immediate,
            EnvelopeId: ThreadEnvelopes.NewEnvelopeId(),
            EnqueuedAtIso: "t0",
            Source: "user",
            SourceThreadId: null,
            Text: "Hi",
            Meta: null));

        orch.ScheduleRun(ThreadIds.Main);

        // Concurrently enqueue another inbox item while the run is in-flight.
        var runTask = orch.RunUntilQuiescentAsync(CancellationToken.None);
        var observeTask = Task.Run(() => orch.ObserveAsync(ThreadIds.Main, new ObservedInboxMessageArrived(
            ThreadId: ThreadIds.Main,
            Kind: ThreadInboxMessageKind.InterThreadMessage,
            Delivery: InboxDelivery.Immediate,
            EnvelopeId: "env_concurrent",
            EnqueuedAtIso: "t1",
            Source: "thread",
            SourceThreadId: "thr_x",
            Text: "concurrent",
            Meta: null), CancellationToken.None));

        await Task.WhenAll(runTask, observeTask);

        // ObserveAsync now queues observations for processing in a wake-driven turn.
        // Run again to ensure the concurrently observed inbox item is reduced+committed.
        orch.ScheduleRun(ThreadIds.Main);
        await orch.RunUntilQuiescentAsync(CancellationToken.None);

        // The committed log must contain both enqueues.
        var committed = threadStore.LoadCommittedEvents(sessionId, ThreadIds.Main);
        committed.OfType<ThreadInboxMessageEnqueued>().Any(e => e.Text == "Hi").Should().BeTrue();
        committed.OfType<ThreadInboxMessageEnqueued>().Any(e => e.EnvelopeId == "env_concurrent").Should().BeTrue();
    }

    private sealed class FakeCaller : IAcpClientCaller
    {
        public ClientCapabilities ClientCapabilities { get; } = new() { Fs = new FileSystemCapabilities() };
        public Task<TResponse> RequestAsync<TRequest, TResponse>(string method, TRequest request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
    }

    private sealed class OneShotChat : MeaiIChatClient
    {
        private int _calls;

        public IAsyncEnumerable<MeaiChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<MeaiChatMessage> messages, MeaiChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            _calls++;
            return _calls switch
            {
                1 => Call1_Tools_NoOp(),
                _ => Call2_Text_Done(),
            };
        }

        private async IAsyncEnumerable<MeaiChatResponseUpdate> Call1_Tools_NoOp()
        {
            yield return new MeaiChatResponseUpdate
            {
                Contents = new List<MeaiAIContent>
                {
                    new MeaiFunctionCallContent("call_0", "report_intent", new Dictionary<string, object?> { ["intent"] = "test" }),
                    new MeaiFunctionCallContent("call_1", "thread_list", new Dictionary<string, object?>()),
                }
            };
        }

        private async IAsyncEnumerable<MeaiChatResponseUpdate> Call2_Text_Done()
        {
            yield return new MeaiChatResponseUpdate { Contents = new List<MeaiAIContent> { new MeaiTextContent("done") } };
        }

        public Task<MeaiChatResponse> GetResponseAsync(IEnumerable<MeaiChatMessage> messages, MeaiChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new MeaiChatResponse(Array.Empty<MeaiChatMessage>()));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
        public Task<string> CompleteAsync(IReadOnlyList<MeaiChatMessage> renderedMessages, CancellationToken cancellationToken) => Task.FromResult("");
    }
}
