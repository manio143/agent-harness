namespace Agent.Harness;

/// <summary>
/// Observed events are produced by the imperative shell (e.g., MEAI streaming updates)
/// and fed into the functional core reducer.
///
/// Observed events are NOT publishable. The core decides what to commit.
/// </summary>
public abstract record ObservedChatEvent
{
    /// <summary>
    /// Lossless raw attachment (e.g. MEAI ChatResponseUpdate) for debugging and provider-specific enrichments.
    /// </summary>
    public object? RawUpdate { get; init; }
}

public sealed record ObservedTurnStarted(string ThreadId) : ObservedChatEvent;

public sealed record ObservedTurnStabilized(string ThreadId) : ObservedChatEvent;

/// <summary>
/// Synthetic harness event used to explicitly trigger a model call at a turn boundary.
/// Used for "enqueue" inbox semantics where a thread should continue processing
/// without becoming idle.
/// </summary>
public sealed record ObservedWakeModel(string ThreadId) : ObservedChatEvent;

public sealed record ObservedUserMessage(string Text) : ObservedChatEvent;

/// <summary>
/// Thread-scoped inbound message observation. The imperative shell emits this when something
/// arrives to a thread (ACP user prompt, thread_send, idle notification, etc.).
/// The reducer commits a ThreadInboxMessageEnqueued.
/// </summary>
public sealed record ObservedInboxMessageArrived(
    string ThreadId,
    Agent.Harness.Threads.ThreadInboxMessageKind Kind,
    Agent.Harness.Threads.InboxDelivery Delivery,
    string EnvelopeId,
    string EnqueuedAtIso,
    string Source,
    string? SourceThreadId,
    string Text,
    System.Collections.Immutable.ImmutableDictionary<string, string>? Meta = null) : ObservedChatEvent;

public sealed record ObservedAssistantTextDelta(string Text) : ObservedChatEvent;

/// <summary>
/// Reasoning/thought delta (e.g., MEAI TextReasoningContent).
/// </summary>
public sealed record ObservedReasoningTextDelta(string Text) : ObservedChatEvent;

public sealed record ObservedReasoningMessageCompleted(string? FinishReason = null) : ObservedChatEvent;

public sealed record ObservedAssistantMessageCompleted(string? FinishReason = null) : ObservedChatEvent;

// --- Tool Call Observations ---
// Invariant: These are fed TO the reducer from the imperative shell (SessionRunner, model provider).
// The reducer decides what to commit based on these observations.

/// <summary>
/// Model/provider detected a tool call request.
/// Invariant: Fed to reducer; reducer commits ToolCallRequested and emits CheckPermission effect.
/// </summary>
public sealed record ObservedToolCallDetected(string ToolId, string ToolName, object Args) : ObservedChatEvent;

/// <summary>
/// Permission check approved (from IAcpClientCaller.RequestPermissionAsync).
/// Invariant: Fed to reducer; reducer commits ToolCallPending and emits ExecuteToolCall effect.
/// </summary>
public sealed record ObservedPermissionApproved(string ToolId, string Reason) : ObservedChatEvent;

/// <summary>
/// Permission check denied.
/// Invariant: Fed to reducer; reducer commits ToolCallRejected, no execution effect emitted.
/// </summary>
public sealed record ObservedPermissionDenied(string ToolId, string Reason) : ObservedChatEvent;

/// <summary>
/// Tool execution progress update.
/// Invariant: Fed from executor; reducer commits ToolCallUpdate.
/// </summary>
public sealed record ObservedToolCallProgressUpdate(string ToolId, object Content) : ObservedChatEvent;

/// <summary>
/// Tool execution completed successfully.
/// Invariant: Fed from executor; reducer commits ToolCallCompleted (terminal state).
/// </summary>
public sealed record ObservedToolCallCompleted(string ToolId, object Result) : ObservedChatEvent;

/// <summary>
/// Tool execution failed.
/// Invariant: Fed from executor; reducer commits ToolCallFailed (terminal state).
/// </summary>
public sealed record ObservedToolCallFailed(string ToolId, string Error) : ObservedChatEvent;

/// <summary>
/// Tool call cancelled (e.g., turn cancellation).
/// Invariant: Fed from executor; reducer commits ToolCallCancelled (terminal state).
/// </summary>
public sealed record ObservedToolCallCancelled(string ToolId) : ObservedChatEvent;

/// <summary>
/// MCP server connection failed during session setup.
/// Invariant: Fed to reducer; session continues gracefully (degraded mode).
/// </summary>
public sealed record ObservedMcpConnectionFailed(string ServerId, string Error) : ObservedChatEvent;

// --- Thread Lifecycle Observations ---
// In the unified orchestrator model, thread lifecycle is owned by ThreadOrchestrator.
// Effects (and other shells) request thread operations by emitting observations.

// NOTE: Thread lifecycle (create/fork) is owned by ThreadOrchestrator and must be invoked via
// dedicated APIs (e.g. RequestForkChildThreadAsync). Lifecycle is intentionally NOT modeled as an
// observed chat event, to keep ObserveAsync observation-only and avoid re-entrancy hazards.
//
// Kept only as a transitional/guarded shape: ObserveAsync rejects this event.
public sealed record ObservedForkChildThreadRequested(
    string ParentThreadId,
    string ChildThreadId,
    System.Collections.Immutable.ImmutableArray<Agent.Harness.SessionEvent> SeedCommitted) : ObservedChatEvent;
