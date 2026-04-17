using System.Collections.Immutable;
using System.Text.Json;

namespace Agent.Harness;

/// <summary>
/// Stable, publishable session events ("committed" events).
/// These are the only events that downstream adapters (ACP/UI) are allowed to publish.
/// </summary>
public abstract record SessionEvent;

public sealed record SessionConfigOptionSet(string ConfigId, string Value) : SessionEvent;

public sealed record UserMessage(string Text) : SessionEvent;
public sealed record AssistantMessage(string Text) : SessionEvent;

// --- Thread prompt first-class message events ---

/// <summary>
/// Message originating from another thread (thread_send / initial message).
/// Renderer maps this to a system message.
/// </summary>
public sealed record InterThreadMessage(string FromThreadId, string Text) : SessionEvent;

/// <summary>
/// Notification that a child thread became idle.
/// Renderer maps this to a system message.
/// </summary>
public sealed record ThreadIdleNotification(string ChildThreadId, string LastIntent) : SessionEvent;
public sealed record ReasoningMessage(string Text) : SessionEvent;

/// <summary>
/// Committed assistant text delta. Useful for streaming modes where we want to publish progress
/// without waiting for message completion.
/// </summary>
public sealed record AssistantTextDelta(string TextDelta) : SessionEvent;

/// <summary>
/// Committed reasoning/thought delta. Publishing is controlled separately from committing.
/// </summary>
public sealed record ReasoningTextDelta(string TextDelta) : SessionEvent;

/// <summary>
/// Committed session metadata update. Metadata files are projections of committed events.
/// </summary>
public sealed record SessionTitleSet(string Title) : SessionEvent;

/// <summary>
/// Debug/test-only committed event that records the exact messages rendered for the model.
/// Must be gated via options and disabled by default.
/// </summary>
public sealed record ModelInvoked(ImmutableArray<ChatMessage> RenderedMessages) : SessionEvent;

// --- Turn lifecycle ---
// Invariant: TurnStart is informative only; TurnEnd is committed when the core decides the turn has stabilized.
public sealed record TurnStarted() : SessionEvent;
public sealed record TurnEnded() : SessionEvent;

// --- Tool Call Events ---
// Invariant: These events are committed by the reducer after observing tool call lifecycle events.
// They form the stable, reproducible record of tool execution.

/// <summary>
/// Tool call detected and permission check requested.
/// Invariant: This commits the intent to call a tool; permission must be checked before execution.
/// </summary>
public sealed record ToolCallRequested(string ToolId, string ToolName, JsonElement Args) : SessionEvent;

/// <summary>
/// Tool call permission approved and queued for execution.
/// Invariant: This state is reached only after permission granted; execution effect emitted.
/// </summary>
public sealed record ToolCallPermissionApproved(string ToolId, string Reason) : SessionEvent;

/// <summary>
/// Tool call permission denied (policy gate).
/// Invariant: Terminal-ish for permission phase; no execution.
/// </summary>
public sealed record ToolCallPermissionDenied(string ToolId, string Reason) : SessionEvent;

public sealed record ToolCallPending(string ToolId) : SessionEvent;

/// <summary>
/// Tool call execution started.
/// Invariant: Execution has begun; further updates expected.
/// </summary>
public sealed record ToolCallInProgress(string ToolId) : SessionEvent;

/// <summary>
/// Incremental tool call output/progress update.
/// Invariant: These are additive; ACP publishes as tool_call_update content appends.
/// </summary>
public sealed record ToolCallUpdate(string ToolId, JsonElement Content) : SessionEvent;

/// <summary>
/// Tool call completed successfully.
/// Invariant: Terminal state; no further updates allowed for this tool call.
/// </summary>
public sealed record ToolCallCompleted(string ToolId, JsonElement Result) : SessionEvent;

/// <summary>
/// Tool call failed during execution.
/// Invariant: Terminal state; failure is observable and doesn't crash session.
/// </summary>
public sealed record ToolCallFailed(string ToolId, string Error) : SessionEvent;

/// <summary>
/// Tool call rejected by user/permission policy.
/// Invariant: Terminal state; tool never executed.
/// </summary>
public sealed record ToolCallRejected(string ToolId, string Reason, ImmutableArray<string> Details) : SessionEvent;

/// <summary>
/// Tool call cancelled (e.g., turn cancellation).
/// Invariant: Terminal state; execution aborted cleanly.
/// </summary>
public sealed record ToolCallCancelled(string ToolId) : SessionEvent;

// --- Threading ---
// Committed to a thread's log when the model calls report_intent.
public sealed record ThreadIntentReported(string Intent) : SessionEvent;

/// <summary>
/// An inbox envelope has been appended to a thread's inbox.
/// </summary>
public sealed record ThreadInboxMessageEnqueued(
    string ThreadId,
    string EnvelopeId,
    Agent.Harness.Threads.ThreadInboxMessageKind Kind,
    ImmutableDictionary<string, string>? Meta,
    string Source,
    string? SourceThreadId,
    string Delivery,
    string EnqueuedAtIso,
    string Text) : SessionEvent;

/// <summary>
/// An inbox envelope has been rendered into the model prompt (made available to the LLM).
/// </summary>
public sealed record ThreadInboxMessageDequeued(
    string ThreadId,
    string EnvelopeId,
    string DequeuedAtIso) : SessionEvent;

public enum ChatRole
{
    System,
    User,
    Assistant,

    /// <summary>
    /// Tool result message (OpenAI/MEAI-style). This should be used to feed tool outputs back to the model
    /// so it can continue and produce final assistant text without re-invoking the same tools.
    /// </summary>
    Tool,
}

public sealed record ChatMessage(ChatRole Role, string Text);
