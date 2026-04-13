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

public sealed record ObservedUserMessage(string Text) : ObservedChatEvent;

public sealed record ObservedAssistantTextDelta(string Text) : ObservedChatEvent;

/// <summary>
/// Reasoning/thought delta (e.g., MEAI TextReasoningContent).
/// </summary>
public sealed record ObservedReasoningTextDelta(string Text) : ObservedChatEvent;

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
