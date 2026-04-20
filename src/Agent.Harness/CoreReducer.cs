using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Collections.Generic;

namespace Agent.Harness;

public sealed record CoreOptions(
    bool EmitModelInvokedEvents = false,
    bool CommitAssistantTextDeltas = false,
    bool CommitReasoningTextDeltas = false);

/// <summary>
/// Functional core reducer.
///
/// Rules (initial slice):
/// - ObservedUserMessage commits a UserMessage and requests CallModel (legacy direct path; normal orchestrator flow uses ObservedInboxMessageArrived + ObservedWakeModel).
/// - ObservedAssistantTextDelta appends to the in-flight assistant buffer.
/// - ObservedAssistantMessageCompleted flushes the assistant buffer into AssistantMessage.
/// - RenderPrompt uses ONLY committed user/assistant messages (debug events are ignored).
/// - RenderToolCatalog filters tools by client capabilities (capability gating).
/// </summary>
public static class Core
{
    private static string ResolveModel(SessionState state)
    {
        // Projection: last SetModel wins.
        // If none exists, we use "default" which will be resolved by the imperative shell.
        var last = state.Committed.OfType<SetModel>().LastOrDefault();
        return last?.Model ?? "default";
    }


    private static bool HasTerminalToolCall(SessionState state, string toolId)
        => state.Committed.Any(e => e switch
        {
            ToolCallCompleted c when c.ToolId == toolId => true,
            ToolCallFailed f when f.ToolId == toolId => true,
            ToolCallCancelled c when c.ToolId == toolId => true,
            ToolCallRejected r when r.ToolId == toolId => true,
            _ => false,
        });

    private static bool IsReportIntentTool(SessionState state, string toolId)
        => state.Committed.OfType<ToolCallRequested>()
            .Any(r => r.ToolId == toolId && r.ToolName == ToolSchemas.ReportIntent.Name);

    private static bool HasOtherOpenToolCalls(SessionState state, string excludingToolId)
    {
        // "Open" means: a ToolCallRequested exists and we haven't committed any terminal event for that toolId yet.
        foreach (var r in state.Committed.OfType<ToolCallRequested>())
        {
            if (r.ToolId == excludingToolId) continue;
            if (!HasTerminalToolCall(state, r.ToolId))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Render the tool catalog based on client capabilities.
    /// Tools requiring unavailable capabilities are filtered out.
    /// </summary>
    public static ImmutableArray<ToolDefinition> RenderToolCatalog(Agent.Acp.Schema.ClientCapabilities capabilities)
    {
        var builder = ImmutableArray.CreateBuilder<ToolDefinition>();

        // Filesystem tools
        if (capabilities.Fs?.ReadTextFile == true)
        {
            builder.Add(ToolSchemas.ReadTextFile);

        }

        if (capabilities.Fs?.WriteTextFile == true)
        {
            builder.Add(ToolSchemas.WriteTextFile);

        }

        // Patch requires BOTH read + write.
        if (capabilities.Fs?.ReadTextFile == true && capabilities.Fs?.WriteTextFile == true)
        {
            builder.Add(ToolSchemas.PatchTextFile);

        }

        // Terminal tools
        if (capabilities.Terminal == true)
        {
            builder.Add(ToolSchemas.ExecuteCommand);

        }

        // Harness-internal tools (always available)
        builder.Add(ToolSchemas.ReportIntent);
        builder.Add(ToolSchemas.ThreadList);
        builder.Add(ToolSchemas.ThreadStart);
        builder.Add(ToolSchemas.ThreadSend);
        builder.Add(ToolSchemas.ThreadRead);
        builder.Add(ToolSchemas.ThreadConfig);

        return builder.ToImmutable();
    }
    public static ReduceResult Reduce(SessionState state, ObservedChatEvent evt, CoreOptions? options = null)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        if (evt is null) throw new ArgumentNullException(nameof(evt));

        switch (evt)
        {
            case ObservedTurnStarted started:
            {
                // New turn (thread-scoped): clear per-turn buffers/policies.
                // IMPORTANT: capture whether the thread was idle BEFORE this turn began.
                // This enables reducer-driven inbox promotion policies without relying on
                // committed TurnStarted markers (which occur inside the turn).
                var startedFromIdle = Agent.Harness.Threads.ThreadStatusProjector.IsIdle(state.Committed);

                var next = state with
                {
                    Buffer = state.Buffer with
                    {
                        IntentReportedThisTurn = false,
                        TurnStartedFromIdle = startedFromIdle,
                    }
                };

                return Commit(next, new TurnStarted());
            }

            case ObservedUserMessage m:
            {
                // Commit the user message AND request a model call.
                var msgEvt = new UserMessage(m.Text);
                var committedMsg = state.Committed.Add(msgEvt);
                var next = state with { Committed = committedMsg };
                return new ReduceResult(
                    next,
                    ImmutableArray.Create<SessionEvent>(msgEvt),
                    ImmutableArray.Create<Effect>(new CallModel(ResolveModel(state))));
            }

            case ObservedInboxMessageArrived arrived:
            {
                var enq = new ThreadInboxMessageEnqueued(
                    ThreadId: arrived.ThreadId,
                    EnvelopeId: arrived.EnvelopeId,
                    Kind: arrived.Kind,
                    Meta: arrived.Meta,
                    Source: arrived.Source,
                    SourceThreadId: arrived.SourceThreadId,
                    Delivery: Agent.Harness.Threads.ThreadInboxDeliveryText.Serialize(arrived.Delivery),
                    EnqueuedAtIso: arrived.EnqueuedAtIso,
                    Text: arrived.Text);

                var committed = state.Committed.Add(enq);
                var next = state with { Committed = committed };

                // Event-driven waking:
                // - Immediate delivery always schedules a wake.
                // - Enqueue delivery schedules a wake only if the thread is currently idle at a wake boundary.
                //   (This replaces imperative polling loops that used to check deliverability.)
                var isIdle = Agent.Harness.Threads.ThreadStatusProjector.IsIdleAtWakeBoundary(committed);

                var effects = arrived.Delivery == Agent.Harness.Threads.InboxDelivery.Immediate
                    ? ImmutableArray.Create<Effect>(new ScheduleWake(arrived.ThreadId))
                    : arrived.Delivery == Agent.Harness.Threads.InboxDelivery.Enqueue && isIdle
                        ? ImmutableArray.Create<Effect>(new ScheduleWake(arrived.ThreadId))
                        : ImmutableArray<Effect>.Empty;

                return new ReduceResult(next, ImmutableArray.Create<SessionEvent>(enq), effects);
            }

            case ObservedWakeModel wake:
            {
                // Wake-time inbox promotion:
                // - immediate is always promotable
                // - enqueue is promotable only if this turn started from an idle state
                //   (i.e., this wake is the first opportunity to run after being idle).
                var (next, newly) = PromotePendingInbox(state, wake.ThreadId, allowEnqueue: state.Buffer.TurnStartedFromIdle);

                // Only call the model if the wake actually produced a prompt-visible message that
                // may require continuation.
                //
                // Design grounding: child thread completion is signaled to the parent via a
                // ThreadIdleNotification. The parent often needs to continue without waiting
                // for user interaction.
                var shouldCallModel = newly.Any(e => e is UserMessage or InterThreadMessage or ThreadIdleNotification or NewThreadTask);
                var effects = shouldCallModel ? ImmutableArray.Create<Effect>(new CallModel(ResolveModel(next))) : ImmutableArray<Effect>.Empty;

                return new ReduceResult(next, newly, effects);
            }

            case ObservedAssistantTextDelta d:
            {
                var next = state with
                {
                    Buffer = state.Buffer with
                    {
                        AssistantMessageOpen = true,
                        AssistantText = state.Buffer.AssistantText + d.Text,
                    },
                };

                if (options?.CommitAssistantTextDeltas == true)
                {
                    var delta = new AssistantTextDelta(d.Text);
                    var committed = next.Committed.Add(delta);
                    return new ReduceResult(
                        next with { Committed = committed },
                        ImmutableArray.Create<SessionEvent>(delta),
                        ImmutableArray<Effect>.Empty);
                }

                return new ReduceResult(next, ImmutableArray<SessionEvent>.Empty, ImmutableArray<Effect>.Empty);
            }

            case ObservedReasoningTextDelta d:
            {
                var next = state with
                {
                    Buffer = state.Buffer with
                    {
                        ReasoningText = state.Buffer.ReasoningText + d.Text,
                        ReasoningMessageOpen = true,
                    }
                };

                if (options?.CommitReasoningTextDeltas == true)
                {
                    var delta = new ReasoningTextDelta(d.Text);
                    var committed = next.Committed.Add(delta);
                    return new ReduceResult(
                        next with { Committed = committed },
                        ImmutableArray.Create<SessionEvent>(delta),
                        ImmutableArray<Effect>.Empty);
                }

                return new ReduceResult(next, ImmutableArray<SessionEvent>.Empty, ImmutableArray<Effect>.Empty);
            }

            case ObservedReasoningMessageCompleted:
                return FlushReasoning(state);

            case ObservedAssistantMessageCompleted:
                return FlushAssistant(state);

            case ObservedTurnStabilized stabilized:
            {
                // Turn stabilization is the reducer's chance to:
                // - deliver pending enqueue inbox items ("only when about to go idle")
                // - and decide whether the turn can truly end.
                //
                // IMPORTANT invariant:
                // - If we emit CallModel from stabilization, we must NOT commit TurnEnded yet.
                //   The turn continues until the model/tool lifecycle stabilizes.
                var (next, newly) = PromotePendingInbox(state, stabilized.ThreadId, allowEnqueue: true);

                var shouldCallModel = newly.Any(e => e is UserMessage or InterThreadMessage or ThreadIdleNotification or NewThreadTask);
                if (shouldCallModel)
                {
                    return new ReduceResult(
                        next,
                        newly,
                        ImmutableArray.Create<Effect>(new CallModel(ResolveModel(next))));
                }

                // Nothing else to do: end the turn.
                var reduced = Commit(next, new TurnEnded());

                var all = ImmutableArray.CreateBuilder<SessionEvent>();
                all.AddRange(newly);
                all.AddRange(reduced.NewlyCommitted);

                return new ReduceResult(reduced.Next, all.ToImmutable(), ImmutableArray<Effect>.Empty);
            }


            // --- Tool Call Lifecycle Observations ---

            case ObservedToolCallDetected detected:
            {
                // Deduplicate: some model/provider streams may repeat tool call content.
                // Invariant: a given toolId must be requested at most once.
                if (state.Committed.Any(e => e is ToolCallRequested r && r.ToolId == detected.ToolId))
                    return new ReduceResult(state, ImmutableArray<SessionEvent>.Empty, ImmutableArray<Effect>.Empty);

                // Always commit the attempted tool call for auditability, even if we reject it later.
                var argsJson = JsonSerializer.SerializeToElement(detected.Args);
                var requested = new ToolCallRequested(detected.ToolId, detected.ToolName, argsJson);

                var committedWithRequested = state.Committed.Add(requested);
                var nextWithRequested = state with
                {
                    Committed = committedWithRequested,
                    Buffer = detected.ToolName == ToolSchemas.ReportIntent.Name
                        ? state.Buffer with { IntentReportedThisTurn = true }
                        : state.Buffer,
                };

                // Policy: the model MUST report intent before calling any other tools in a turn.
                if (detected.ToolName != ToolSchemas.ReportIntent.Name && !state.Buffer.IntentReportedThisTurn)
                {
                    var rejected = new ToolCallRejected(detected.ToolId, "missing_report_intent", ImmutableArray.Create("must_call:report_intent"));
                    var reduced = Commit(nextWithRequested, rejected);
                    return new ReduceResult(
                        reduced.Next,
                        ImmutableArray.Create<SessionEvent>(requested, rejected),
                        ImmutableArray.Create<Effect>(new CallModel(ResolveModel(reduced.Next))));
                }

                // Early rejection: unknown tool / invalid args
                var tool = state.Tools.FirstOrDefault(t => t.Name == detected.ToolName);
                if (tool is null)
                {
                    var rejected = new ToolCallRejected(detected.ToolId, "unknown_tool", ImmutableArray.Create("unknown_tool"));
                    var reduced = Commit(nextWithRequested, rejected);
                    return new ReduceResult(
                        reduced.Next,
                        ImmutableArray.Create<SessionEvent>(requested, rejected),
                        ImmutableArray.Create<Effect>(new CallModel(ResolveModel(reduced.Next))));
                }

                var errors = ToolArgValidator.Validate(tool.InputSchema, detected.Args);
                if (!errors.IsEmpty)
                {
                    var rejected = new ToolCallRejected(detected.ToolId, "invalid_args", errors);
                    var reduced = Commit(nextWithRequested, rejected);
                    return new ReduceResult(
                        reduced.Next,
                        ImmutableArray.Create<SessionEvent>(requested, rejected),
                        ImmutableArray.Create<Effect>(new CallModel(ResolveModel(reduced.Next))));
                }

                // Valid call: request permission.
                var permissionEffect = new CheckPermission(detected.ToolId, detected.ToolName, detected.Args);

                return new ReduceResult(
                    nextWithRequested,
                    ImmutableArray.Create<SessionEvent>(requested),
                    ImmutableArray.Create<Effect>(permissionEffect));
            }

            case ObservedPermissionApproved approved:
            {
                // Commit ToolCallPermissionApproved + ToolCallPending and emit ExecuteToolCall effect
                var approvedEvt = new ToolCallPermissionApproved(approved.ToolId, approved.Reason);
                var pending = new ToolCallPending(approved.ToolId);

                var committed = state.Committed.Add(approvedEvt).Add(pending);

                // Find the original ToolCallRequested to get tool name and args
                var requestedEvent = state.Committed
                    .OfType<ToolCallRequested>()
                    .FirstOrDefault(r => r.ToolId == approved.ToolId);

                if (requestedEvent is null)
                {
                    // Should not happen in well-formed sessions
                    return new ReduceResult(state, ImmutableArray<SessionEvent>.Empty, ImmutableArray<Effect>.Empty);
                }

                var executeEffect = new ExecuteToolCall(
                    approved.ToolId,
                    requestedEvent.ToolName,
                    requestedEvent.Args);

                var next = state with { Committed = committed };
                return new ReduceResult(
                    next,
                    ImmutableArray.Create<SessionEvent>(approvedEvt, pending),
                    ImmutableArray.Create<Effect>(executeEffect));
            }

            case ObservedPermissionDenied denied:
            {
                // Commit ToolCallPermissionDenied.
                var deniedEvt = new ToolCallPermissionDenied(denied.ToolId, denied.Reason);
                var committed = state.Committed.Add(deniedEvt);
                var next = state with { Committed = committed };

                // Tightened invariant: never commit a ToolCallRejected unless we have a prior ToolCallRequested.
                // (Otherwise events.jsonl would contain a rejection with no information about the attempted call.)
                var hasRequested = state.Committed.OfType<ToolCallRequested>().Any(r => r.ToolId == denied.ToolId);
                if (!hasRequested)
                {
                    return new ReduceResult(
                        next,
                        ImmutableArray.Create<SessionEvent>(deniedEvt),
                        ImmutableArray.Create<Effect>(new CallModel(ResolveModel(next))));
                }

                var rejected = new ToolCallRejected(denied.ToolId, denied.Reason, ImmutableArray<string>.Empty);
                var reduced = Commit(next, rejected);

                return new ReduceResult(
                    reduced.Next,
                    ImmutableArray.Create<SessionEvent>(deniedEvt, rejected),
                    ImmutableArray.Create<Effect>(new CallModel(ResolveModel(reduced.Next))));
            }

            case ObservedToolCallProgressUpdate progress:
            {
                var committed = state.Committed;
                
                // Check if we need to transition from Pending to InProgress
                var hasInProgress = state.Committed
                    .OfType<ToolCallInProgress>()
                    .Any(ip => ip.ToolId == progress.ToolId);
                
                if (!hasInProgress)
                {
                    // First progress update - transition to InProgress first
                    var inProgress = new ToolCallInProgress(progress.ToolId);
                    committed = committed.Add(inProgress);
                }
                
                // Commit ToolCallUpdate for incremental updates
                var update = new ToolCallUpdate(progress.ToolId, JsonSerializer.SerializeToElement(progress.Content));
                committed = committed.Add(update);
                var next = state with { Committed = committed };
                
                // NewlyCommitted might include InProgress + Update
                var newlyCommitted = !hasInProgress
                    ? ImmutableArray.Create<SessionEvent>(new ToolCallInProgress(progress.ToolId), update)
                    : ImmutableArray.Create<SessionEvent>(update);
                
                return new ReduceResult(
                    next,
                    newlyCommitted,
                    ImmutableArray<Effect>.Empty);
            }

            case ObservedToolCallCompleted completed:
            {
                // Idempotency: if a terminal event already exists for this toolId, ignore duplicates.
                if (HasTerminalToolCall(state, completed.ToolId))
                    return new ReduceResult(state, ImmutableArray<SessionEvent>.Empty, ImmutableArray<Effect>.Empty);

                // Commit ToolCallCompleted (terminal state) then request a model call.
                var completedEvent = new ToolCallCompleted(completed.ToolId, JsonSerializer.SerializeToElement(completed.Result));
                
                // If this is report_intent, also commit ThreadIntentReported
                var newlyCommitted = ImmutableArray.CreateBuilder<SessionEvent>();
                newlyCommitted.Add(completedEvent);
                
                var isReportIntent = IsReportIntentTool(state, completed.ToolId);
                if (isReportIntent)
                {
                    // Extract intent from the original tool call request
                    var toolCall = state.Committed.OfType<ToolCallRequested>()
                        .FirstOrDefault(t => t.ToolId == completed.ToolId);
                    
                    if (toolCall is not null && toolCall.Args.TryGetProperty("intent", out var intentProp))
                    {
                        var intent = intentProp.GetString();
                        if (!string.IsNullOrEmpty(intent))
                        {
                            newlyCommitted.Add(new ThreadIntentReported(intent));
                        }
                    }
                }
                
                var committed = state.Committed.AddRange(newlyCommitted);
                var next = state with { Committed = committed };

                // Report-intent should normally not force a re-prompt if the model already streamed
                // additional tool intents in the same response.
                // However if report_intent is the ONLY thing the model emitted, we must re-prompt.
                var hasOtherOpen = HasOtherOpenToolCalls(state, completed.ToolId);

                var effects = (isReportIntent && hasOtherOpen)
                    ? ImmutableArray<Effect>.Empty
                    : ImmutableArray.Create<Effect>(new CallModel(ResolveModel(next)));

                return new ReduceResult(
                    next,
                    newlyCommitted.ToImmutable(),
                    effects);
            }

            case ObservedToolCallFailed failed:
            {
                if (HasTerminalToolCall(state, failed.ToolId))
                    return new ReduceResult(state, ImmutableArray<SessionEvent>.Empty, ImmutableArray<Effect>.Empty);

                // Commit ToolCallFailed (terminal state) then request a model call.
                var failedEvent = new ToolCallFailed(failed.ToolId, failed.Error);
                var committed = state.Committed.Add(failedEvent);
                var next = state with { Committed = committed };

                return new ReduceResult(
                    next,
                    ImmutableArray.Create<SessionEvent>(failedEvent),
                    ImmutableArray.Create<Effect>(new CallModel(ResolveModel(next))));
            }

            case ObservedToolCallCancelled cancelled:
            {
                if (HasTerminalToolCall(state, cancelled.ToolId))
                    return new ReduceResult(state, ImmutableArray<SessionEvent>.Empty, ImmutableArray<Effect>.Empty);

                // Commit ToolCallCancelled (terminal state) then request a model call.
                var cancelledEvent = new ToolCallCancelled(cancelled.ToolId);
                var committed = state.Committed.Add(cancelledEvent);
                var next = state with { Committed = committed };

                return new ReduceResult(
                    next,
                    ImmutableArray.Create<SessionEvent>(cancelledEvent),
                    ImmutableArray.Create<Effect>(new CallModel(ResolveModel(next))));
            }

            case ObservedSetModel set:
            {
                var evtSet = new SetModel(set.Model);
                var committed = state.Committed.Add(evtSet);
                var next = state with { Committed = committed };
                return new ReduceResult(next, ImmutableArray.Create<SessionEvent>(evtSet), ImmutableArray<Effect>.Empty);
            }

            default:
                return new ReduceResult(state, ImmutableArray<SessionEvent>.Empty, ImmutableArray<Effect>.Empty);
        }
    }

    public static ImmutableArray<ChatMessage> RenderPrompt(SessionState state)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));

        var builder = ImmutableArray.CreateBuilder<ChatMessage>();
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        foreach (var evt in state.Committed)
        {
            switch (evt)
            {
                case UserMessage u:
                    builder.Add(new ChatMessage(ChatRole.User, u.Text));
                    break;

                case InterThreadMessage it:
                    builder.Add(new ChatMessage(ChatRole.System, $"<inter_thread from=\"{it.FromThreadId}\">{it.Text}</inter_thread>"));
                    break;

                case ThreadIdleNotification n:
                    builder.Add(new ChatMessage(ChatRole.System, $"<thread_idle child=\"{n.ChildThreadId}\" intent=\"{n.LastIntent}\" />"));
                    break;

                case AssistantMessage a:
                    builder.Add(new ChatMessage(ChatRole.Assistant, a.Text));
                    break;

                case ToolCallRequested t:
                {
                    var payload = JsonSerializer.Serialize(new { toolId = t.ToolId, toolName = t.ToolName, args = t.Args }, json);
                    builder.Add(new ChatMessage(ChatRole.System, $"<tool_call>{payload}</tool_call>"));
                    break;
                }

                case ToolCallUpdate u:
                {
                    var payload = JsonSerializer.Serialize(new { toolId = u.ToolId, content = u.Content }, json);
                    builder.Add(new ChatMessage(ChatRole.System, $"<tool_update>{payload}</tool_update>"));
                    break;
                }

                case ToolCallCompleted c:
                {
                    // Feed tool results back to the model in a tool-role message (MEAI/OpenAI style).
                    // This is more reliable than embedding results inside system XML-ish tags.
                    var payload = JsonSerializer.Serialize(new { toolId = c.ToolId, outcome = "completed", result = c.Result }, json);
                    builder.Add(new ChatMessage(ChatRole.Tool, payload));
                    break;
                }

                case ToolCallFailed f:
                {
                    var payload = JsonSerializer.Serialize(new { toolId = f.ToolId, outcome = "failed", error = f.Error }, json);
                    builder.Add(new ChatMessage(ChatRole.Tool, payload));
                    break;
                }

                case ToolCallRejected r:
                {
                    var payload = JsonSerializer.Serialize(new { toolId = r.ToolId, outcome = "rejected", reason = r.Reason, details = r.Details }, json);
                    builder.Add(new ChatMessage(ChatRole.Tool, payload));
                    break;
                }

                case ToolCallCancelled c:
                {
                    var payload = JsonSerializer.Serialize(new { toolId = c.ToolId, outcome = "cancelled" }, json);
                    builder.Add(new ChatMessage(ChatRole.Tool, payload));
                    break;
                }

                case SetModel m:
                {
                    builder.Add(new ChatMessage(ChatRole.System, $"Inference model has been set to: {m.Model}."));
                    break;
                }

                default:
                    // Ignore other committed events for prompt rendering.
                    break;
            }
        }

        return builder.ToImmutable();
    }

    private static ReduceResult Commit(SessionState state, SessionEvent evt)
    {
        var committed = state.Committed.Add(evt);
        var next = state with { Committed = committed };
        return new ReduceResult(next, ImmutableArray.Create(evt), ImmutableArray<Effect>.Empty);
    }

    private static (SessionState Next, ImmutableArray<SessionEvent> NewlyCommitted) PromotePendingInbox(
        SessionState state,
        string currentThreadId,
        bool allowEnqueue)
    {

        var enq = state.Committed
            .OfType<ThreadInboxMessageEnqueued>()
            .Where(e => e.ThreadId == currentThreadId)
            .ToList();
        if (enq.Count == 0)
            return (state, ImmutableArray<SessionEvent>.Empty);

        var dq = state.Committed.OfType<ThreadInboxMessageDequeued>()
            .Select(d => d.EnvelopeId)
            .ToHashSet(StringComparer.Ordinal);

        var pending = enq
            .Where(e => !dq.Contains(e.EnvelopeId))
            // immediate: always promotable
            // enqueue: promotable only at the boundaries where the reducer permits it
            .Where(e => Agent.Harness.Threads.ThreadInboxDeliveryText.IsImmediate(e.Delivery)
                || (allowEnqueue && Agent.Harness.Threads.ThreadInboxDeliveryText.IsEnqueue(e.Delivery)))
            .OrderBy(e => e.EnqueuedAtIso)
            .ThenBy(e => e.EnvelopeId)
            .ToList();

        if (pending.Count == 0)
            return (state, ImmutableArray<SessionEvent>.Empty);

        var builder = ImmutableArray.CreateBuilder<SessionEvent>();
        var committed = state.Committed;
        var hasNewThreadTask = committed.Any(e => e is NewThreadTask);

        foreach (var e in pending)
        {
            // Mark dequeued.
            var deq = new ThreadInboxMessageDequeued(
                ThreadId: e.ThreadId,
                EnvelopeId: e.EnvelopeId,
                DequeuedAtIso: DateTimeOffset.UtcNow.ToString("O"));
            committed = committed.Add(deq);
            builder.Add(deq);

            // Promote to a first-class message event.
            switch (e.Kind)
            {
                case Agent.Harness.Threads.ThreadInboxMessageKind.UserPrompt:
                    var user = new UserMessage(e.Text);
                    committed = committed.Add(user);
                    builder.Add(user);
                    break;

                case Agent.Harness.Threads.ThreadInboxMessageKind.InterThreadMessage:
                    var it = new InterThreadMessage(FromThreadId: e.SourceThreadId ?? "", Text: e.Text);
                    committed = committed.Add(it);
                    builder.Add(it);
                    break;

                case Agent.Harness.Threads.ThreadInboxMessageKind.ThreadIdleNotification:
                {
                    var childId = e.Meta is not null && e.Meta.TryGetValue(Agent.Harness.Threads.ThreadInboxMetaKeys.ChildThreadId, out var c) ? c : (e.SourceThreadId ?? "");
                    var lastIntent = e.Meta is not null && e.Meta.TryGetValue(Agent.Harness.Threads.ThreadInboxMetaKeys.LastIntent, out var i) ? i : "";
                    var idle = new ThreadIdleNotification(ChildThreadId: childId, LastIntent: lastIntent);
                    committed = committed.Add(idle);
                    builder.Add(idle);
                    break;
                }

                case Agent.Harness.Threads.ThreadInboxMessageKind.NewThreadTask:
                {
                    // Invariant: a thread should have exactly one NewThreadTask bootstrap marker.
                    // If a duplicate arrives (e.g., replay/bug), dequeue it but do not commit a second marker.
                    if (hasNewThreadTask)
                        break;

                    var parentId = e.Meta is not null && e.Meta.TryGetValue(Agent.Harness.Threads.ThreadInboxMetaKeys.ParentThreadId, out var p)
                        ? p
                        : (e.SourceThreadId ?? "");

                    var isFork = e.Meta is not null
                        && e.Meta.TryGetValue(Agent.Harness.Threads.ThreadInboxMetaKeys.IsFork, out var f)
                        && bool.TryParse(f, out var fork)
                        && fork;

                    var task = new NewThreadTask(ThreadId: e.ThreadId, ParentThreadId: parentId, IsFork: isFork, Message: e.Text);
                    committed = committed.Add(task);
                    builder.Add(task);
                    hasNewThreadTask = true;
                    break;
                }

                default:
                    // Unknown kind: treat as inter-thread system message for now.
                    var fallback = new InterThreadMessage(FromThreadId: e.SourceThreadId ?? "", Text: e.Text);
                    committed = committed.Add(fallback);
                    builder.Add(fallback);
                    break;
            }
        }

        return (state with { Committed = committed }, builder.ToImmutable());
    }

    private static ReduceResult FlushAssistant(SessionState state)
    {
        if (!state.Buffer.AssistantMessageOpen && string.IsNullOrEmpty(state.Buffer.AssistantText))
        {
            // Nothing to flush.
            return new ReduceResult(state, ImmutableArray<SessionEvent>.Empty, ImmutableArray<Effect>.Empty);
        }

        var text = state.Buffer.AssistantText;
        var nextState = state with
        {
            Buffer = state.Buffer with { AssistantText = "", AssistantMessageOpen = false }
        };

        // Commit even if text is empty: treat boundary as a message completion.
        // (We can tighten this later if desired.)
        return Commit(nextState, new AssistantMessage(text));
    }

    private static ReduceResult FlushReasoning(SessionState state)
    {
        if (!state.Buffer.ReasoningMessageOpen && string.IsNullOrEmpty(state.Buffer.ReasoningText))
            return new ReduceResult(state, ImmutableArray<SessionEvent>.Empty, ImmutableArray<Effect>.Empty);

        var text = state.Buffer.ReasoningText;
        var nextState = state with
        {
            Buffer = state.Buffer with { ReasoningText = "", ReasoningMessageOpen = false }
        };

        return Commit(nextState, new ReasoningMessage(text));
    }
}
