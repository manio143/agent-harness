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
    private readonly bool _logLlmPrompts;
    private readonly ISessionStore _sessionStore;

    private bool _toolsInitialized;
    private ImmutableArray<ToolDefinition> _toolCatalog;
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
        bool logLlmPrompts,
        ISessionStore sessionStore,
        IThreadStore threadStore,
        ThreadManager threads)
    {
        _sessionId = sessionId;
        _client = client;
        _chat = chat;
        _mcp = mcp;
        _coreOptions = coreOptions;
        _logLlmPrompts = logLlmPrompts;
        _sessionStore = sessionStore;

        _toolsInitialized = false;
        _toolCatalog = ImmutableArray<ToolDefinition>.Empty;
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

    public Task RunUntilQuiescentAsync(CancellationToken cancellationToken)
        => RunUntilQuiescentAsync(sinkFactory: null, cancellationToken);

    public async Task RunUntilQuiescentAsync(Func<string, IEventSink?>? sinkFactory, CancellationToken cancellationToken)
    {
        // Drain queued thread runs until there is nothing left runnable.
        // Invariant: per-thread gate prevents overlapping runs.
        for (var i = 0; i < 10_000; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_runQueue.TryDequeue(out var threadId))
                return;

            _queued.TryRemove(threadId, out _);
            await RunOneTurnIfNeededAsync(threadId, sinkFactory, cancellationToken).ConfigureAwait(false);

            // If more work was queued by this run, loop.
        }

        throw new InvalidOperationException("thread_orchestrator_quiescence_loop_limit_exceeded");
    }

    public void InitializeToolCatalog(ImmutableArray<ToolDefinition> tools)
    {
        if (_toolsInitialized)
            throw new InvalidOperationException("tool_catalog_already_initialized");

        _toolCatalog = tools;
        _toolsInitialized = true;
    }

    private ImmutableArray<ToolDefinition> GetToolCatalogSnapshot()
    {
        if (!_toolsInitialized)
            throw new InvalidOperationException("tool_catalog_not_initialized");
        return _toolCatalog;
    }

    public async Task ObserveAsync(string threadId, ObservedChatEvent observed, CancellationToken cancellationToken = default)
    {
        // Observe can be called concurrently with RunOneTurnIfNeededAsync. Protect the in-memory
        // state cache with the same per-thread gate used for execution.
        var gate = _gates.GetOrAdd(threadId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var tools = GetToolCatalogSnapshot();
            var initial = _states.GetOrAdd(threadId, _ => SessionState.Empty with { Tools = tools });
            initial = initial with { Tools = tools };

            var reduced = Core.Reduce(initial, observed);

            foreach (var evt in reduced.NewlyCommitted)
                _threadStore.AppendCommittedEvent(_sessionId, threadId, evt);

            _states[threadId] = reduced.Next;

            // If reducer requested a wake/model call, schedule the thread.
            if (reduced.Effects.Any(e => e is CallModel or ScheduleWake))
                ScheduleRun(threadId);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task RunOneTurnIfNeededAsync(string threadId, Func<string, IEventSink?>? sinkFactory, CancellationToken cancellationToken)
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

            var tools = GetToolCatalogSnapshot();
            var initial = _states.GetOrAdd(threadId, _ => SessionState.Empty with { Tools = tools });
            initial = initial with { Tools = tools };

            // Always refresh committed history from the store before executing a wake.
            // Other threads may have appended inbox enqueues (e.g. idle notifications / enqueue delivery)
            // directly into this thread’s committed log.
            var committedNow = _threadStore.LoadCommittedEvents(_sessionId, threadId);
            initial = initial with { Committed = committedNow, Buffer = TurnBuffer.Empty };

            // Run a single turn kicked off by wake.
            async IAsyncEnumerable<ObservedChatEvent> WakeObserved()
            {
                yield return new ObservedWakeModel(threadId);
            }

            var titleGen = new SessionTitleGenerator(new Llm.MeaiTitleChatClientAdapter(_chat));
            var acpClient = threadId == ThreadIds.Main ? _client : NullAcpClientCaller.Instance;

            var effects = new AcpEffectExecutor(
                _sessionId,
                acpClient,
                _chat,
                _mcp,
                logLlmPrompts: threadId == ThreadIds.Main && _logLlmPrompts,
                sessionCwd: _sessionStore.TryLoadMetadata(_sessionId)?.Cwd,
                store: _sessionStore,
                threads: _threads,
                scheduler: this,
                threadId: threadId);

            var runner = new SessionRunner(_coreOptions, titleGen, effects);
            var sink = sinkFactory?.Invoke(threadId) ?? new ThreadEventSink(_sessionId, threadId, _threadStore);

            var result = await runner.RunTurnAsync(threadId, initial, WakeObserved(), cancellationToken, sink: sink).ConfigureAwait(false);
            _states[threadId] = result.Next;

            // Event-driven waking: reducer emits ScheduleWake effects when a wake is needed.
            // No imperative polling/rescheduling here.

            // Fully idle: notify parent (immediate).
            await NotifyParentIfChildFullyIdleAsync(threadId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task NotifyParentIfChildFullyIdleAsync(string threadId, CancellationToken cancellationToken)
    {
        var meta = _threadStore.TryLoadThreadMetadata(_sessionId, threadId);
        if (meta?.ParentThreadId is null) return;

        // Only notify if truly nothing pending.
        if (_threads.HasImmediateOrDeliverableEnqueue(threadId))
            return;

        var intent = meta.Intent ?? string.Empty;
        // Main thread is just another thread in the thread store, but only the ACP layer should
        // project/publish main-thread committed events.
        // So: persist the parent inbox enqueue directly to the thread store, without running the
        // main thread inside the child-thread orchestrator.
        var metaDict = ImmutableDictionary.CreateRange(new Dictionary<string, string>
        {
            ["childThreadId"] = threadId,
            ["lastIntent"] = intent,
        });

        var enq = new ThreadInboxMessageEnqueued(
            ThreadId: meta.ParentThreadId,
            EnvelopeId: ThreadEnvelopes.NewEnvelopeId(),
            Kind: ThreadInboxMessageKind.ThreadIdleNotification,
            Meta: metaDict,
            Source: "thread",
            SourceThreadId: threadId,
            Delivery: "immediate",
            EnqueuedAtIso: DateTimeOffset.UtcNow.ToString("O"),
            Text: $"Child thread became idle. Last intent: {intent}");

        _threadStore.AppendCommittedEvent(_sessionId, meta.ParentThreadId, enq);

        // Ensure parent gets a chance to process the notification without user interaction.
        ScheduleRun(meta.ParentThreadId);

    }
}
