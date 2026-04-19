using System.Collections.Immutable;
using Agent.Acp.Acp;
using Agent.Acp.Schema;
using Agent.Harness.Acp;
using Agent.Harness.Persistence;
using Agent.Harness.Threads;
using FluentAssertions;
using Microsoft.Extensions.AI;

namespace Agent.Harness.Tests;

public sealed class ThreadOrchestratorIdleNotificationPendingWorkInvariantTests
{
    [Fact]
    public async Task ChildIdleNotification_IsNotEnqueuedToParent_WhileChildHasPendingObservedWork()
    {
        var sessionId = "s1";

        var sessionRoot = Path.Combine(Path.GetTempPath(), "harness-orchestrator-idle-tests", Guid.NewGuid().ToString("N"));
        var sessionStore = new JsonlSessionStore(sessionRoot);
        sessionStore.CreateNew(sessionId, new SessionMetadata(
            SessionId: sessionId,
            Cwd: "/tmp",
            Title: null,
            CreatedAtIso: DateTimeOffset.UtcNow.ToString("O"),
            UpdatedAtIso: DateTimeOffset.UtcNow.ToString("O")));

        var threadStore = new InMemoryThreadStore();
        var threads = new ThreadManager(sessionId, threadStore);

        var childId = "thr_child";
        var now = DateTimeOffset.UtcNow.ToString("O");
        threadStore.CreateThread(sessionId, new ThreadMetadata(
            ThreadId: childId,
            ParentThreadId: ThreadIds.Main,
            Intent: "doing work",
            CreatedAtIso: now,
            UpdatedAtIso: now,
            Model: null));

        var chat = new MinimalChatClient();
        var orchestrator = new ThreadOrchestrator(
            sessionId: sessionId,
            client: new FakeClientCaller(),
            chat: chat,
            chatByModel: _ => chat,
            quickWorkModel: "default",
            mcp: NullMcpToolInvoker.Instance,
            coreOptions: new CoreOptions(),
            logLlmPrompts: false,
            sessionStore: sessionStore,
            threadStore: threadStore,
            threadAppender: threadStore,
            threads: threads);

        orchestrator.InitializeToolCatalog(ImmutableArray.Create(
            ToolSchemas.ReportIntent,
            ToolSchemas.ThreadList,
            ToolSchemas.ThreadStart,
            ToolSchemas.ThreadSend,
            ToolSchemas.ThreadRead));

        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        IEventSink SinkFactory(string tid)
        {
            if (tid != childId) return new ThreadEventSink(sessionId, tid, threadStore, threadStore);

            var inner = new ThreadEventSink(sessionId, tid, threadStore, threadStore);
            return new BlockingOnTurnEndedSink(inner, ended, release);
        }

        // Schedule child.
        orchestrator.ScheduleRun(childId);

        // Run orchestrator; when child commits TurnEnded, inject a new observed inbox message into the child.
        var runTask = orchestrator.RunUntilQuiescentAsync(SinkFactory, CancellationToken.None);

        await ended.Task;

        // Inject work into the child AFTER it ended its current turn but BEFORE it notifies parent.
        await orchestrator.ObserveAsync(childId, new ObservedInboxMessageArrived(
            ThreadId: childId,
            Kind: ThreadInboxMessageKind.InterThreadMessage,
            Delivery: InboxDelivery.Immediate,
            EnvelopeId: ThreadEnvelopes.NewEnvelopeId(),
            EnqueuedAtIso: DateTimeOffset.UtcNow.ToString("O"),
            Source: "thread",
            SourceThreadId: "thr_sender",
            Text: "ping",
            Meta: null));

        release.SetResult();
        await runTask;

        // Invariant: we should get exactly ONE idle notification, after the follow-up child work drains.
        var parent = threadStore.LoadCommittedEvents(sessionId, ThreadIds.Main);
        parent.OfType<ThreadInboxMessageEnqueued>()
            .Count(e => e.Kind == ThreadInboxMessageKind.ThreadIdleNotification)
            .Should().Be(1);
    }

    private sealed class BlockingOnTurnEndedSink : IEventSink
    {
        private readonly IEventSink _inner;
        private readonly TaskCompletionSource _ended;
        private readonly TaskCompletionSource _release;

        public BlockingOnTurnEndedSink(IEventSink inner, TaskCompletionSource ended, TaskCompletionSource release)
        {
            _inner = inner;
            _ended = ended;
            _release = release;
        }

        public ValueTask OnObservedAsync(ObservedChatEvent observed, CancellationToken cancellationToken = default)
            => _inner.OnObservedAsync(observed, cancellationToken);

        public async ValueTask OnCommittedAsync(SessionEvent committed, CancellationToken cancellationToken = default)
        {
            await _inner.OnCommittedAsync(committed, cancellationToken);

            if (committed is TurnEnded)
            {
                _ended.TrySetResult();
                await _release.Task;
            }
        }
    }

    private sealed class FakeClientCaller : IAcpClientCaller
    {
        public ClientCapabilities ClientCapabilities { get; } = new() { Fs = new FileSystemCapabilities() };

        public Task<TResponse> RequestAsync<TRequest, TResponse>(string method, TRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class MinimalChatClient : IChatClient
    {
        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(Array.Empty<Microsoft.Extensions.AI.ChatMessage>()));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate
            {
                Contents = new List<AIContent>
                {
                    new FunctionCallContent("call_0", "report_intent", new Dictionary<string, object?> { ["intent"] = "noop" }),
                    new Microsoft.Extensions.AI.TextContent("ok"),
                }
            };

            await Task.CompletedTask;
        }
    }
}
