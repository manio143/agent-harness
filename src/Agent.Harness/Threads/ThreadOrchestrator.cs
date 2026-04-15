using System.Collections.Concurrent;
using System.Collections.Immutable;
using Agent.Harness.Acp;
using Agent.Harness.Persistence;
using Agent.Harness.TitleGeneration;

namespace Agent.Harness.Threads;

/// <summary>
/// In-process, event-driven orchestrator for running child threads.
///
/// Guarantees:
/// - At most one model call in-flight per thread (via per-thread gate).
/// - Child thread execution is kicked off by message delivery scheduling.
/// - Parent is notified only when a child is fully idle (no pending deliverable work).
/// </summary>
public sealed class ThreadOrchestrator : IThreadScheduler
{
    private readonly string _sessionId;
    private readonly IAcpClientCaller _client;
    private readonly Microsoft.Extensions.AI.IChatClient _chat;
    private readonly IMcpToolInvoker _mcp;
    private readonly CoreOptions _coreOptions;
    private readonly ISessionStore _sessionStore;
    private readonly IThreadStore _threadStore;
    private readonly ThreadManager _threads;

    private readonly ConcurrentDictionary<string, SessionState> _states = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();
    private readonly ConcurrentDictionary<string, int> _scheduled = new();

    public ThreadOrchestrator(
        string sessionId,
        IAcpClientCaller client,
        Microsoft.Extensions.AI.IChatClient chat,
        IMcpToolInvoker mcp,
        CoreOptions coreOptions,
        ISessionStore sessionStore,
        IThreadStore threadStore,
        ThreadManager threads)
    {
        _sessionId = sessionId;
        _client = client;
        _chat = chat;
        _mcp = mcp;
        _coreOptions = coreOptions;
        _sessionStore = sessionStore;
        _threadStore = threadStore;
        _threads = threads;
    }

    public void ScheduleRun(string threadId)
    {
        // Idempotent scheduling: only one pending run at a time.
        if (_scheduled.TryAdd(threadId, 1))
        {
            _ = Task.Run(async () =>
            {
                try { await RunOneTurnIfNeededAsync(threadId, CancellationToken.None).ConfigureAwait(false); }
                finally { _scheduled.TryRemove(threadId, out _); }
            });
        }
    }

    private async Task RunOneTurnIfNeededAsync(string threadId, CancellationToken cancellationToken)
    {
        var gate = _gates.GetOrAdd(threadId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Mark idle boundary before checking deliverability.
            _threads.MarkIdle(threadId);

            // If nothing to do, emit idle notification to parent (if child).
            if (!_threads.HasImmediateOrDeliverableEnqueue(threadId))
            {
                NotifyParentIfChildFullyIdle(threadId);
                return;
            }

            var meta = _threadStore.TryLoadThreadMetadata(_sessionId, threadId);
            var parentId = meta?.ParentThreadId;

            var initial = _states.GetOrAdd(threadId, _ => SessionState.Empty);

            // Run a single turn kicked off by wake.
            async IAsyncEnumerable<ObservedChatEvent> WakeObserved()
            {
                yield return new ObservedWakeModel();
            }

            var titleGen = new SessionTitleGenerator(new Llm.MeaiTitleChatClientAdapter(_chat));
            var effects = new AcpEffectExecutor(
                _sessionId,
                _client,
                _chat,
                _mcp,
                logLlmPrompts: false,
                sessionCwd: _sessionStore.TryLoadMetadata(_sessionId)?.Cwd,
                store: _sessionStore,
                threads: _threads,
                scheduler: this,
                threadId: threadId);

            var runner = new SessionRunner(_coreOptions, titleGen, effects);
            var sink = new ThreadEventSink(_sessionId, threadId, _threadStore);

            var result = await runner.RunTurnAsync(initial, WakeObserved(), cancellationToken, sink: sink).ConfigureAwait(false);
            _states[threadId] = result.Next;

            // Turn ended: if more deliverable work exists, reschedule.
            _threads.MarkIdle(threadId);
            if (_threads.HasImmediateOrDeliverableEnqueue(threadId))
            {
                ScheduleRun(threadId);
                return;
            }

            // Fully idle: notify parent (immediate).
            NotifyParentIfChildFullyIdle(threadId);
        }
        finally
        {
            gate.Release();
        }
    }

    private void NotifyParentIfChildFullyIdle(string threadId)
    {
        var meta = _threadStore.TryLoadThreadMetadata(_sessionId, threadId);
        if (meta?.ParentThreadId is null) return;

        // Only notify if truly nothing pending.
        if (_threads.HasImmediateOrDeliverableEnqueue(threadId))
            return;

        var intent = meta.Intent ?? string.Empty;
        _threadStore.AppendInbox(_sessionId, meta.ParentThreadId, new ThreadEnvelope(
            Source: "thread",
            SourceThreadId: threadId,
            Text: $"Child thread became idle. Last intent: {intent}",
            Delivery: InboxDelivery.Immediate,
            EnqueuedAtIso: DateTimeOffset.UtcNow.ToString("O")));

        // Nudge parent to run if it wants immediate processing.
        ScheduleRun(meta.ParentThreadId);
    }
}
