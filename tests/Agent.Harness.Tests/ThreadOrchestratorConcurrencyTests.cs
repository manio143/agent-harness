using System.Collections.Immutable;
using Agent.Acp.Acp;
using Agent.Acp.Schema;
using Agent.Harness.Acp;
using Agent.Harness.Persistence;
using Agent.Harness.Threads;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Xunit;

namespace Agent.Harness.Tests;

public sealed class ThreadOrchestratorConcurrencyTests
{
    [Fact]
    public async Task ScheduleRun_BeforeRunning_IsIdempotent_PerThread()
    {
        var (o, sessionId, threadStore) = CreateOrchestrator();
        o.SetToolCatalog(ImmutableArray<ToolDefinition>.Empty);

        for (var i = 0; i < 50; i++)
            o.ScheduleRun(ThreadIds.Main);

        await o.RunUntilQuiescentAsync(CancellationToken.None);

        var committed = threadStore.LoadCommittedEvents(sessionId, ThreadIds.Main);
        committed.OfType<TurnStarted>().Should().HaveCount(1);
        committed.OfType<TurnEnded>().Should().HaveCount(1);
    }

    [Fact]
    public async Task ScheduleRun_DuringInFlight_IsIdempotent_AndRunsExactlyOneFollowUpTurn()
    {
        var (o, sessionId, threadStore) = CreateOrchestrator();
        o.SetToolCatalog(ImmutableArray<ToolDefinition>.Empty);

        var blocking = new BlockingSink(new ThreadEventSink(sessionId, ThreadIds.Main, threadStore));

        o.ScheduleRun(ThreadIds.Main);
        var running = o.RunUntilQuiescentAsync(sinkFactory: _ => blocking, CancellationToken.None);

        await blocking.FirstCommitEntered.Task;

        // While a turn is in-flight, multiple schedules should still enqueue only ONE follow-up.
        for (var i = 0; i < 50; i++)
            o.ScheduleRun(ThreadIds.Main);

        blocking.Release.SetResult();
        await running;

        var committed = threadStore.LoadCommittedEvents(sessionId, ThreadIds.Main);
        committed.OfType<TurnStarted>().Should().HaveCount(2);
        committed.OfType<TurnEnded>().Should().HaveCount(2);
    }

    [Fact]
    public async Task ObserveAsync_WhenGateHeld_CompletesSynchronously_AndEnqueuesRun()
    {
        var (o, _, threadStore) = CreateOrchestrator();
        o.SetToolCatalog(ImmutableArray<ToolDefinition>.Empty);

        var blocking = new BlockingSink(new ThreadEventSink("s1", ThreadIds.Main, threadStore));

        o.ScheduleRun(ThreadIds.Main);
        var running = o.RunUntilQuiescentAsync(sinkFactory: _ => blocking, CancellationToken.None);

        await blocking.FirstCommitEntered.Task;

        // Gate is held inside the turn. ObserveAsync must not block waiting for it.
        var observe = o.ObserveAsync(ThreadIds.Main, new ObservedReasoningTextDelta("x"));
        observe.IsCompletedSuccessfully.Should().BeTrue();

        // ObserveAsync schedules a follow-up run, which should be visible immediately.
        o.HasPendingWork.Should().BeTrue();

        blocking.Release.SetResult();
        await running;
    }

    private sealed class BlockingSink : IEventSink
    {
        private int _committedCount;

        public BlockingSink(IEventSink inner)
        {
            _inner = inner;
        }

        private readonly IEventSink _inner;

        public TaskCompletionSource FirstCommitEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask OnObservedAsync(ObservedChatEvent observed, CancellationToken cancellationToken = default)
            => _inner.OnObservedAsync(observed, cancellationToken);

        public async ValueTask OnCommittedAsync(SessionEvent committed, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _committedCount) == 1)
            {
                FirstCommitEntered.TrySetResult();
                await Release.Task.ConfigureAwait(false);
            }

            await _inner.OnCommittedAsync(committed, cancellationToken).ConfigureAwait(false);
        }
    }

    private static (ThreadOrchestrator Orchestrator, string SessionId, InMemoryThreadStore ThreadStore) CreateOrchestrator()
    {
        var root = Path.Combine(Path.GetTempPath(), "orchestrator-concurrency", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var sessionId = "s1";
        var sessionStore = new JsonlSessionStore(root);
        sessionStore.CreateNew(sessionId, new SessionMetadata(sessionId, "/repo", null, "2026-01-01T00:00:00Z", "2026-01-01T00:00:00Z"));

        var threadStore = new InMemoryThreadStore();
        var threads = new ThreadManager(sessionId, threadStore);

        var chat = new NoopChatClient();

        var o = new ThreadOrchestrator(
            sessionId: sessionId,
            client: new StubAcpClientCaller(new ClientCapabilities()),
            chat: chat,
            chatByModel: _ => chat,
            quickWorkModel: "default",
            mcp: NullMcpToolInvoker.Instance,
            coreOptions: new CoreOptions(),
            logLlmPrompts: false,
            sessionStore: sessionStore,
            threadStore: threadStore,
            threads: threads);

        return (o, sessionId, threadStore);
    }

    private sealed class NoopChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse());

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            return YieldNone();

            static async IAsyncEnumerable<ChatResponseUpdate> YieldNone()
            {
                await Task.CompletedTask;
                yield break;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class StubAcpClientCaller : IAcpClientCaller
    {
        public StubAcpClientCaller(ClientCapabilities caps) => ClientCapabilities = caps;

        public ClientCapabilities ClientCapabilities { get; }

        public Task<TResponse> RequestAsync<TRequest, TResponse>(string method, TRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Client caller not used in this test.");
    }
}
