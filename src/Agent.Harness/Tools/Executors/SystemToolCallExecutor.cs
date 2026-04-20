using System.Collections.Immutable;
using System.Text.Json;

namespace Agent.Harness.Tools.Executors;

public sealed class SystemToolCallExecutor : IToolCallExecutor
{
    private readonly Agent.Harness.Threads.IThreadTools? _threadTools;
    private readonly Agent.Harness.Threads.IThreadObserver? _observer;
    private readonly Agent.Harness.Threads.IThreadLifecycle? _lifecycle;
    private readonly Agent.Harness.Threads.IThreadScheduler? _scheduler;
    private readonly Func<string, bool>? _isKnownModel;
    private readonly string _threadId;

    public SystemToolCallExecutor(
        Agent.Harness.Threads.IThreadTools? threadTools,
        Agent.Harness.Threads.IThreadObserver? observer,
        Agent.Harness.Threads.IThreadLifecycle? lifecycle,
        Agent.Harness.Threads.IThreadScheduler? scheduler,
        Func<string, bool>? isKnownModel,
        string threadId)
    {
        _threadTools = threadTools;
        _observer = observer;
        _lifecycle = lifecycle;
        _scheduler = scheduler;
        _isKnownModel = isKnownModel;
        _threadId = threadId;
    }

    public bool CanExecute(string toolName)
        => toolName is "report_intent" or "thread_list" or "thread_read" or "thread_send" or "thread_start" or "thread_config";

    public async Task<ImmutableArray<ObservedChatEvent>> ExecuteAsync(SessionState state, ExecuteToolCall tool, CancellationToken cancellationToken)
    {
        try
        {
            var args = Agent.Harness.Tools.ToolArgs.Normalize(tool.Args);

            switch (tool.ToolName)
            {
                case "report_intent":
                {
                    var intent = GetRequiredString(args, "intent");
                    _threadTools?.ReportIntent(_threadId, intent);

                    return ImmutableArray.Create<ObservedChatEvent>(
                        new ObservedToolCallCompleted(tool.ToolId, JsonSerializer.SerializeToElement(new { ok = true })));
                }

                case "thread_list":
                {
                    var threads = _threadTools?.List() ?? ImmutableArray<Agent.Harness.Threads.ThreadInfo>.Empty;
                    return ImmutableArray.Create<ObservedChatEvent>(new ObservedToolCallCompleted(
                        tool.ToolId,
                        JsonSerializer.SerializeToElement(new { threads })));
                }

                case "thread_read":
                {
                    var threadId = GetRequiredString(args, "threadId");
                    var messages = _threadTools?.ReadThreadMessages(threadId) ?? ImmutableArray<Agent.Harness.Threads.ThreadMessage>.Empty;
                    return ImmutableArray.Create<ObservedChatEvent>(new ObservedToolCallCompleted(
                        tool.ToolId,
                        JsonSerializer.SerializeToElement(new { messages })));
                }

                case "thread_config":
                {
                    var targetThreadId = args.TryGetValue("threadId", out var tidVal) && tidVal.ValueKind == JsonValueKind.String
                        ? tidVal.GetString()!
                        : _threadId;

                    // Read-only: return current projected model.
                    if (!args.TryGetValue("model", out var modelVal) || modelVal.ValueKind != JsonValueKind.String)
                    {
                        var current = targetThreadId == _threadId
                            ? ResolveModelFromCommitted(state.Committed)
                            : _threadTools?.GetModel(targetThreadId) ?? "default";

                        return ImmutableArray.Create<ObservedChatEvent>(new ObservedToolCallCompleted(
                            tool.ToolId,
                            JsonSerializer.SerializeToElement(new { threadId = targetThreadId, model = current })));
                    }

                    var model = modelVal.GetString()!;
                    if (string.IsNullOrWhiteSpace(model))
                        throw new InvalidOperationException("thread_config.model_required");

                    if (!string.Equals(model, "default", StringComparison.OrdinalIgnoreCase) && _isKnownModel is not null && !_isKnownModel(model))
                        throw new InvalidOperationException("thread_config.unknown_model");

                    // Cross-thread: persist immediately via orchestrator lifecycle.
                    if (targetThreadId != _threadId)
                    {
                        if (_lifecycle is null)
                            throw new InvalidOperationException("thread_tools_require_orchestrator");

                        await _lifecycle.RequestSetThreadModelAsync(targetThreadId, model, cancellationToken).ConfigureAwait(false);

                        return ImmutableArray.Create<ObservedChatEvent>(
                            new ObservedToolCallCompleted(tool.ToolId, JsonSerializer.SerializeToElement(new { ok = true, threadId = targetThreadId, model })));
                    }

                    return ImmutableArray.Create<ObservedChatEvent>(
                        new ObservedSetModel(targetThreadId, model),
                        new ObservedToolCallCompleted(tool.ToolId, JsonSerializer.SerializeToElement(new { ok = true, threadId = targetThreadId, model })));
                }

                case "thread_start":
                {
                    var name = GetRequiredString(args, "name");
                    var context = GetRequiredString(args, "context");
                    var message = GetRequiredString(args, "message");
                    var delivery = ParseDelivery(args);

                    if (!IsValidThreadName(name))
                        throw new InvalidOperationException("thread_start.invalid_name");

                    if (string.Equals(name, Agent.Harness.Threads.ThreadIds.Main, StringComparison.Ordinal))
                        throw new InvalidOperationException("thread_start.name_reserved");

                    if (_lifecycle is null || _observer is null || _scheduler is null)
                        throw new InvalidOperationException("thread_tools_require_orchestrator");

                    ImmutableArray<SessionEvent> seed = context switch
                    {
                        "new" => ImmutableArray<SessionEvent>.Empty,
                        // Forking should carry over historical context, but must not copy prior thread bootstrap markers
                        // (e.g., parent's NewThreadTask) into the child.
                        "fork" => state.Committed.Where(e => e is not NewThreadTask).ToImmutableArray(),
                        _ => throw new InvalidOperationException("thread_start.invalid_context"),
                    };

                    var id = name;

                    await _lifecycle.RequestForkChildThreadAsync(
                        _threadId,
                        id,
                        seed,
                        cancellationToken).ConfigureAwait(false);

                    if (args.TryGetValue("model", out var modelVal) && modelVal.ValueKind == JsonValueKind.String)
                    {
                        var model = modelVal.GetString()!;
                        if (string.IsNullOrWhiteSpace(model))
                            throw new InvalidOperationException("thread_start.model_required");

                        if (!string.Equals(model, "default", StringComparison.OrdinalIgnoreCase) && _isKnownModel is not null && !_isKnownModel(model))
                            throw new InvalidOperationException("thread_start.unknown_model");

                        await _observer.ObserveAsync(id, new ObservedSetModel(id, model), cancellationToken).ConfigureAwait(false);
                    }

                    await _observer.ObserveAsync(
                        id,
                        Agent.Harness.Threads.ThreadInboxArrivals.NewThreadTask(
                            threadId: id,
                            parentThreadId: _threadId,
                            isFork: string.Equals(context, "fork", StringComparison.Ordinal),
                            message: message,
                            delivery: delivery),
                        cancellationToken).ConfigureAwait(false);

                    if (delivery == Agent.Harness.Threads.InboxDelivery.Immediate)
                        _scheduler.ScheduleRun(id);

                    return ImmutableArray.Create<ObservedChatEvent>(new ObservedToolCallCompleted(
                        tool.ToolId,
                        JsonSerializer.SerializeToElement(new { threadId = id })));
                }

                case "thread_send":
                {
                    var threadId = GetRequiredString(args, "threadId");
                    var message = GetRequiredString(args, "message");
                    var delivery = ParseDelivery(args);

                    var inboxArrived = Agent.Harness.Threads.ThreadInboxArrivals.InterThreadMessage(
                        threadId: threadId,
                        text: message,
                        sourceThreadId: _threadId,
                        source: "thread",
                        delivery: delivery);

                    // Special-case: sending to self should not require orchestrator. We surface the arrival
                    // as a local observation so the core turn can commit it.
                    if (threadId == _threadId)
                    {
                        return ImmutableArray.Create<ObservedChatEvent>(
                            inboxArrived,
                            new ObservedToolCallCompleted(tool.ToolId, JsonSerializer.SerializeToElement(new { ok = true })));
                    }

                    // Cross-thread requires orchestrator wiring.
                    if (_observer is null || _scheduler is null)
                        throw new InvalidOperationException("thread_tools_require_orchestrator");

                    await _observer.ObserveAsync(threadId, inboxArrived, cancellationToken).ConfigureAwait(false);

                    if (delivery == Agent.Harness.Threads.InboxDelivery.Immediate)
                        _scheduler.ScheduleRun(threadId);

                    return ImmutableArray.Create<ObservedChatEvent>(
                        new ObservedToolCallCompleted(tool.ToolId, JsonSerializer.SerializeToElement(new { ok = true })));
                }

                default:
                    return ImmutableArray.Create<ObservedChatEvent>(new ObservedToolCallFailed(tool.ToolId, "unknown_tool"));
            }
        }
        catch (Exception ex)
        {
            return ImmutableArray.Create<ObservedChatEvent>(new ObservedToolCallFailed(tool.ToolId, ex.Message));
        }
    }

    private static Agent.Harness.Threads.InboxDelivery ParseDelivery(Dictionary<string, JsonElement> args)
    {
        if (!args.TryGetValue("delivery", out var el) || el.ValueKind != JsonValueKind.String)
            return Agent.Harness.Threads.InboxDelivery.Immediate;

        var v = el.GetString();
        return Agent.Harness.Threads.ThreadInboxDeliveryText.Parse(v);
    }

    private static string GetRequiredString(Dictionary<string, JsonElement> obj, string name)
    {
        if (!obj.TryGetValue(name, out var v) || v.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException($"missing_required:{name}");

        return v.GetString() ?? "";
    }

    private static bool IsValidThreadName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name.Length > 64) return false;

        // Keep this strict: thread id becomes a directory name in JsonlThreadStore.
        // Allow: letters, digits, '_' and '-'. Must start with a letter.
        if (!char.IsLetter(name[0])) return false;

        foreach (var ch in name)
        {
            if (char.IsLetterOrDigit(ch)) continue;
            if (ch is '_' or '-') continue;
            return false;
        }

        return true;
    }

    private static string ResolveModelFromCommitted(ImmutableArray<SessionEvent> committed)
    {
        // "default" means: use the configured DefaultModel for this session.
        // The executor will interpret it.
        var last = committed.OfType<SetModel>().LastOrDefault();
        return last?.Model ?? "default";
    }
}
