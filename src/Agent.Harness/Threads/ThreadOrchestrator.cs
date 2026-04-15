using System.Collections.Concurrent;
using System.Collections.Immutable;
using Agent.Acp.Acp;
using Agent.Acp.Schema;
using Agent.Harness;
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
    private readonly ConcurrentQueue<string> _runQueue = new();
    private readonly ConcurrentDictionary<string, byte> _queued = new();

    private sealed class NullAcpClientCaller : IAcpClientCaller
    {
        public static readonly NullAcpClientCaller Instance = new();

        public ClientCapabilities ClientCapabilities { get; } = new() { Fs = new FileSystemCapabilities() };

        public Task<TResponse> RequestAsync<TRequest, TResponse>(string method, TRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException($"NullAcpClientCaller should not be used for method: {method}");
    }

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
        // Idempotent scheduling: only one queued run per thread.
        if (_queued.TryAdd(threadId, 0))
            _runQueue.Enqueue(threadId);
    }

    public bool HasPendingWork => !_runQueue.IsEmpty;

    public async Task RunUntilQuiescentAsync(CancellationToken cancellationToken)
    {
        // Drain queued thread runs until there is nothing left runnable.
        // Invariant: per-thread gate prevents overlapping runs.
        for (var i = 0; i < 10_000; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_runQueue.TryDequeue(out var threadId))
                return;

            _queued.TryRemove(threadId, out _);
            await RunOneTurnIfNeededAsync(threadId, cancellationToken).ConfigureAwait(false);

            // If more work was queued by this run, loop.
        }

        throw new InvalidOperationException("thread_orchestrator_quiescence_loop_limit_exceeded");
    }

    public void Observe(string threadId, ObservedChatEvent observed)
    {
        var initial = _states.GetOrAdd(threadId, _ => SessionState.Empty with
        {
            Tools = ImmutableArray.Create(
                ToolSchemas.ReportIntent,
                ToolSchemas.ThreadList,
                ToolSchemas.ThreadNew,
                ToolSchemas.ThreadFork,
                ToolSchemas.ThreadSend,
                ToolSchemas.ThreadRead),
        });

        var reduced = Core.Reduce(initial, observed);

        foreach (var evt in reduced.NewlyCommitted)
            _threadStore.AppendCommittedEvent(_sessionId, threadId, evt);

        _states[threadId] = reduced.Next;

        // If reducer requested a model call, schedule the thread.
        if (reduced.Effects.Any(e => e is CallModel))
            ScheduleRun(threadId);
    }

    private async Task RunOneTurnIfNeededAsync(string threadId, CancellationToken cancellationToken)
    {
        var gate = _gates.GetOrAdd(threadId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Thread status is derived from committed turn markers (TurnStarted/TurnEnded).

            // Run is explicitly scheduled when work is expected (e.g. CallModel requested by reducer).
            // Do not gate execution purely on inbox state: inbox may already have been dequeued/promoted
            // into first-class events (and still require a model call).
            var meta = _threadStore.TryLoadThreadMetadata(_sessionId, threadId);
            var parentId = meta?.ParentThreadId;

            var initial = _states.GetOrAdd(threadId, _ => SessionState.Empty with
            {
                Tools = ImmutableArray.Create(
                    ToolSchemas.ReportIntent,
                    ToolSchemas.ThreadList,
                    ToolSchemas.ThreadNew,
                    ToolSchemas.ThreadFork,
                    ToolSchemas.ThreadSend,
                    ToolSchemas.ThreadRead),
            });

            // Run a single turn kicked off by wake.
            async IAsyncEnumerable<ObservedChatEvent> WakeObserved()
            {
                yield return new ObservedWakeModel();
            }

            var titleGen = new SessionTitleGenerator(new Llm.MeaiTitleChatClientAdapter(_chat));
            var acpClient = threadId == ThreadIds.Main ? _client : NullAcpClientCaller.Instance;

            var effects = new AcpEffectExecutor(
                _sessionId,
                acpClient,
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
        // Enqueue parent notification via ThreadManager so it is recorded consistently.
        var metaDict = ImmutableDictionary.CreateRange(new Dictionary<string, string>
        {
            ["childThreadId"] = threadId,
            ["lastIntent"] = intent,
        });

        Observe(meta.ParentThreadId, new ObservedInboxMessageArrived(
            ThreadId: meta.ParentThreadId,
            Kind: ThreadInboxMessageKind.ThreadIdleNotification,
            Delivery: InboxDelivery.Immediate,
            EnvelopeId: ThreadEnvelopes.NewEnvelopeId(),
            EnqueuedAtIso: DateTimeOffset.UtcNow.ToString("O"),
            Source: "thread",
            SourceThreadId: threadId,
            Text: $"Child thread became idle. Last intent: {intent}",
            Meta: metaDict));

    }
}
