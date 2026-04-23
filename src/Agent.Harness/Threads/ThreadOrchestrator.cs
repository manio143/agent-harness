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
public sealed class ThreadOrchestrator : IThreadObserver, IThreadLifecycle, IThreadScheduler, IThreadTools
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
    private readonly Func<string, Microsoft.Extensions.AI.IChatClient> _chatByModel;
    private readonly Func<string, string?>? _providerModelByFriendlyName;
    private readonly string _quickWorkModel;
    private readonly Func<string, bool>? _isKnownModel;
    private readonly IMcpToolInvoker _mcp;
    private readonly CoreOptions _coreOptions;
    private readonly bool _logLlmPrompts;
    private readonly ISessionStore _sessionStore;
    private readonly string? _modelCatalogSystemPrompt;
    private readonly int _compactionTailMessageCount;
    private readonly int? _compactionMaxTailMessageChars;
    private readonly string _compactionModel;

    private bool _toolsInitialized;
    private ImmutableArray<ToolDefinition> _toolCatalog;
    private readonly IThreadStore _threadStore;
    private readonly IThreadCommittedEventAppender _threadAppender;
    private readonly ThreadManager _threads;

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();

    private readonly ConcurrentDictionary<string, ConcurrentQueue<ObservedChatEvent>> _observedQueues = new();

    public ThreadOrchestrator(
        string sessionId,
        IAcpClientCaller client,
        Microsoft.Extensions.AI.IChatClient chat,
        Func<string, Microsoft.Extensions.AI.IChatClient> chatByModel,
        string quickWorkModel,
        IMcpToolInvoker mcp,
        CoreOptions coreOptions,
        bool logLlmPrompts,
        ISessionStore sessionStore,
        IThreadStore threadStore,
        IThreadCommittedEventAppender threadAppender,
        ThreadManager threads,
        Func<string, bool>? isKnownModel = null,
        string? modelCatalogSystemPrompt = null,
        Func<string, string?>? providerModelByFriendlyName = null,
        int compactionTailMessageCount = 5,
        int? compactionMaxTailMessageChars = null,
        string compactionModel = "default")
    {
        _sessionId = sessionId;
        _client = client;
        _chat = chat;
        _chatByModel = chatByModel;
        _quickWorkModel = quickWorkModel;
        _providerModelByFriendlyName = providerModelByFriendlyName;
        _isKnownModel = isKnownModel;
        _mcp = mcp;
        _coreOptions = coreOptions;
        _logLlmPrompts = logLlmPrompts;
        _sessionStore = sessionStore;
        _modelCatalogSystemPrompt = modelCatalogSystemPrompt;
        _compactionTailMessageCount = compactionTailMessageCount;
        _compactionMaxTailMessageChars = compactionMaxTailMessageChars;
        _compactionModel = compactionModel;

        _toolsInitialized = false;
        _toolCatalog = ImmutableArray<ToolDefinition>.Empty;
        _threadStore = threadStore;
        _threadAppender = threadAppender;
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
        // Back-compat: older call sites used "Initialize" even though the catalog may legitimately change
        // over the lifetime of a session (e.g. config option updates / allowlists).
        SetToolCatalog(tools);
    }

    public void SetToolCatalog(ImmutableArray<ToolDefinition> tools)
    {
        _toolCatalog = tools;
        _toolsInitialized = true;
    }

    private ImmutableArray<ToolDefinition> GetToolCatalogSnapshot()
    {
        if (!_toolsInitialized)
            throw new InvalidOperationException("tool_catalog_not_initialized");
        return _toolCatalog;
    }

    /// <summary>
    /// Re-entrant-safe observation enqueue API.
    /// NOTE: This method must never block on the per-thread execution gate.
    /// </summary>
    public Task ObserveAsync(string threadId, ObservedChatEvent observed, CancellationToken cancellationToken = default)
    {
        // In the unified model, ObserveAsync never persists committed events directly.
        // It simply enqueues observed events for the target thread and schedules a wake-driven turn.
        // IMPORTANT: ObserveAsync must be re-entrant safe.
        // It can be called while a turn is running (e.g. tools that enqueue follow-up work). It must never
        // block on the per-thread execution gate, otherwise self-send / nested observations can deadlock.

        var q = _observedQueues.GetOrAdd(threadId, _ => new ConcurrentQueue<ObservedChatEvent>());
        q.Enqueue(observed);

        // Ensure the thread will run soon to reduce+commit the observation.
        ScheduleRun(threadId);

        return Task.CompletedTask;
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
            if (meta is not null && !string.IsNullOrWhiteSpace(meta.ClosedAtIso))
                return;

            var parentId = meta?.ParentThreadId;

            var tools = GetToolCatalogSnapshot();
            var initial = SessionState.Empty with { Tools = tools };

            // Always refresh committed history from the store before executing a wake.
            // Other threads may have appended inbox enqueues (e.g. idle notifications / enqueue delivery)
            // directly into this thread’s committed log.
            var committedNow = _threadStore.LoadCommittedEvents(_sessionId, threadId);
            initial = initial with { Committed = committedNow, Buffer = TurnBuffer.Empty };

            // Run a single turn kicked off by wake.
            async IAsyncEnumerable<ObservedChatEvent> WakeObserved()
            {
                // Drain any queued observations first.
                if (_observedQueues.TryGetValue(threadId, out var q))
                {
                    while (q.TryDequeue(out var obs))
                    {
                        yield return obs;
                    }
                }

                // Always include a wake marker to allow inbox promotion + turn boundary processing.
                yield return new ObservedWakeModel(threadId);
            }

            var titleGen = new SessionTitleGenerator(_chatByModel(_quickWorkModel));
            var acpClient = threadId == ThreadIds.Main ? _client : NullAcpClientCaller.Instance;

            var effects = new HarnessEffectExecutor(
                _sessionId,
                acpClient,
                _chat,
                chatByModel: _chatByModel,
                providerModelByFriendlyName: _providerModelByFriendlyName,
                isKnownModel: _isKnownModel,
                _mcp,
                logLlmPrompts: threadId == ThreadIds.Main && _logLlmPrompts,
                sessionCwd: _sessionStore.TryLoadMetadata(_sessionId)?.Cwd,
                store: _sessionStore,
                modelCatalogSystemPrompt: _modelCatalogSystemPrompt,
                compactionTailMessageCount: _compactionTailMessageCount,
                compactionMaxTailMessageChars: _compactionMaxTailMessageChars,
                compactionModel: _compactionModel,
                threadTools: this,
                observer: this,
                lifecycle: this,
                scheduler: this,
                threadId: threadId);

            var runner = new SessionRunner(_coreOptions, titleGen, effects);
            var sink = sinkFactory?.Invoke(threadId) ?? new ThreadEventSink(_sessionId, threadId, _threadStore, _threadAppender);

            await runner.RunTurnAsync(threadId, initial, WakeObserved(), cancellationToken, sink: sink).ConfigureAwait(false);

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

    public Task RequestForkChildThreadAsync(
        string parentThreadId,
        string childThreadId,
        ThreadMode mode,
        ImmutableArray<SessionEvent> seedCommitted,
        CancellationToken cancellationToken = default)
    {
        return ForkChildThreadAsync(parentThreadId, childThreadId, mode, seedCommitted, cancellationToken);
    }

    public async Task RequestSetThreadModelAsync(
        string threadId,
        string model,
        CancellationToken cancellationToken = default)
    {
        // Persist via sink to preserve the "sink-only" invariant.
        var sink = threadId == ThreadIds.Main
            ? (IEventSink)new MainThreadEventSink(_sessionId, _threadStore, _threadAppender, _sessionStore, logObserved: false)
            : new ThreadEventSink(_sessionId, threadId, _threadStore, _threadAppender);

        await sink.OnCommittedAsync(new SetModel(model), cancellationToken).ConfigureAwait(false);
    }

    public Task RequestStopThreadAsync(
        string threadId,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        var meta = _threadStore.TryLoadThreadMetadata(_sessionId, threadId);
        if (meta is null)
            throw new InvalidOperationException($"unknown_thread:{threadId}");

        if (!string.IsNullOrWhiteSpace(meta.ClosedAtIso))
            return Task.CompletedTask;

        var now = DateTimeOffset.UtcNow.ToString("O");
        _threadStore.SaveThreadMetadata(_sessionId, meta with
        {
            ClosedAtIso = now,
            ClosedReason = string.IsNullOrWhiteSpace(reason) ? "stopped" : reason,
            UpdatedAtIso = now,
        });

        // Best-effort: notify parent immediately, including last assistant message.
        // (Even if the child is currently running, this provides immediate context.)
        return NotifyParentThreadStoppedAsync(threadId, cancellationToken);
    }

    private async Task ForkChildThreadAsync(
        string parentThreadId,
        string childThreadId,
        ThreadMode mode,
        ImmutableArray<SessionEvent> seedCommitted,
        CancellationToken cancellationToken)
    {
        // Create child thread metadata (owned by orchestrator).
        var now = DateTimeOffset.UtcNow.ToString("O");
        var meta = new ThreadMetadata(
            ThreadId: childThreadId,
            ParentThreadId: parentThreadId,
            Intent: null,
            CreatedAtIso: now,
            UpdatedAtIso: now,
            Mode: mode,
            Model: ResolveModelFromCommitted(seedCommitted),
            CompactionCount: 0);

        _threadStore.CreateThread(_sessionId, meta);

        // Seed child committed history from the provided parent snapshot.
        // We intentionally do NOT read from the store here, because callers may request a fork mid-turn
        // (after new commits exist in memory but before they hit the store).
        var sink = new ThreadEventSink(_sessionId, childThreadId, _threadStore, _threadAppender);
        foreach (var evt in seedCommitted)
            await sink.OnCommittedAsync(evt, cancellationToken).ConfigureAwait(false);

        // New thread may have work to do if follow-up observations arrive; schedule is observation-driven.
    }

    private static string ResolveModelFromCommitted(ImmutableArray<SessionEvent> committed)
        => committed.OfType<SetModel>().Select(m => m.Model).LastOrDefault() ?? "default";

    public ImmutableArray<ThreadInfo> List() => _threads.List();

    public ImmutableArray<ThreadMessage> ReadThreadMessages(string threadId) => _threads.ReadThreadMessages(threadId);

    public void ReportIntent(string threadId, string intent) => _threads.ReportIntent(threadId, intent);

    public string GetModel(string threadId) => _threads.GetModel(threadId);

    public ThreadMetadata? TryGetThreadMetadata(string threadId) => _threads.TryGetThreadMetadata(threadId);

    private async Task NotifyParentIfChildFullyIdleAsync(string threadId, CancellationToken cancellationToken)
    {
        var meta = _threadStore.TryLoadThreadMetadata(_sessionId, threadId);
        if (meta?.ParentThreadId is null) return;

        // Closed threads are terminal; do not emit idle notifications.
        if (!string.IsNullOrWhiteSpace(meta.ClosedAtIso))
            return;

        // Only notify if truly nothing pending.
        // "Pending" includes:
        // - committed inbox items (enqueued but not yet dequeued/promoted)
        // - observed-but-not-yet-reduced work (queued observations / queued run)
        //
        // Otherwise we can incorrectly signal "idle" while there is follow-up work already scheduled.
        if (_threads.HasAnyPendingInbox(threadId))
            return;

        // If a new observation was enqueued during this thread's execution, ObserveAsync will have
        // scheduled a follow-up run. Do not notify parent until that follow-up drains.
        if (_queued.ContainsKey(threadId))
            return;

        if (_observedQueues.TryGetValue(threadId, out var q) && !q.IsEmpty)
            return;

        var intent = meta.Intent ?? string.Empty;
        var lastAssistant = GetLastAssistantMessageText(threadId);
        var lastAssistantSnippet = Truncate(lastAssistant, maxChars: 500);

        // IMPORTANT invariant: committed events MUST only be appended from within the reducer loop.
        // So we do not write to the parent's committed log directly here.
        // Instead, we observe an inbox arrival for the parent and let the reducer commit it.
        var metaDict = ImmutableDictionary.CreateRange(new Dictionary<string, string>
        {
            [ThreadInboxMetaKeys.ChildThreadId] = threadId,
            [ThreadInboxMetaKeys.LastIntent] = intent,
            ["lastAssistantMessage"] = lastAssistantSnippet,
        });

        var arrived = new ObservedInboxMessageArrived(
            ThreadId: meta.ParentThreadId,
            Kind: ThreadInboxMessageKind.ThreadIdleNotification,
            Delivery: InboxDelivery.Immediate,
            EnvelopeId: ThreadEnvelopes.NewEnvelopeId(),
            EnqueuedAtIso: DateTimeOffset.UtcNow.ToString("O"),
            Source: "thread",
            SourceThreadId: threadId,
            Text: $"Child thread became idle. Last intent: {intent}\nLast assistant: {lastAssistantSnippet}",
            Meta: metaDict);

        await ObserveAsync(meta.ParentThreadId, arrived, cancellationToken).ConfigureAwait(false);

        // Single-mode threads are one-shot: once they reach full idle they are closed and removed from the list.
        if (meta.Mode == ThreadMode.Single)
        {
            var now = DateTimeOffset.UtcNow.ToString("O");
            _threadStore.SaveThreadMetadata(_sessionId, meta with
            {
                ClosedAtIso = now,
                ClosedReason = "completed",
                UpdatedAtIso = now,
            });
        }
    }

    private Task NotifyParentThreadStoppedAsync(string threadId, CancellationToken cancellationToken)
    {
        var meta = _threadStore.TryLoadThreadMetadata(_sessionId, threadId);
        if (meta?.ParentThreadId is null) return Task.CompletedTask;

        var intent = meta.Intent ?? string.Empty;
        var reason = meta.ClosedReason ?? "stopped";
        var lastAssistant = GetLastAssistantMessageText(threadId);
        var lastAssistantSnippet = Truncate(lastAssistant, maxChars: 500);

        var metaDict = ImmutableDictionary.CreateRange(new Dictionary<string, string>
        {
            [ThreadInboxMetaKeys.ChildThreadId] = threadId,
            [ThreadInboxMetaKeys.LastIntent] = intent,
            ["closedReason"] = reason,
            ["lastAssistantMessage"] = lastAssistantSnippet,
        });

        var arrived = new ObservedInboxMessageArrived(
            ThreadId: meta.ParentThreadId,
            Kind: ThreadInboxMessageKind.ThreadIdleNotification,
            Delivery: InboxDelivery.Immediate,
            EnvelopeId: ThreadEnvelopes.NewEnvelopeId(),
            EnqueuedAtIso: DateTimeOffset.UtcNow.ToString("O"),
            Source: "thread",
            SourceThreadId: threadId,
            Text: $"Child thread was stopped. Reason: {reason}. Last intent: {intent}\nLast assistant: {lastAssistantSnippet}",
            Meta: metaDict);

        return ObserveAsync(meta.ParentThreadId, arrived, cancellationToken);
    }

    private string GetLastAssistantMessageText(string threadId)
    {
        var committed = _threadStore.LoadCommittedEvents(_sessionId, threadId);
        return committed.OfType<AssistantMessage>().LastOrDefault()?.Text ?? string.Empty;
    }

    private static string Truncate(string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        if (text.Length <= maxChars) return text;
        return text.Substring(0, maxChars) + $"… [truncated, original_length={text.Length}]";
    }
}
