namespace Agent.Acp.Client.AvaloniaApp.Domain.Conversation;

public abstract record ChatItem;

public sealed record ChatText(string Text) : ChatItem;

/// <summary>Local user message bubble.</summary>
public sealed record ChatUserText(string Text) : ChatItem;

/// <summary>System/timeline separator (e.g., reconnect boundaries).</summary>
public sealed record ChatSystemText(string Text) : ChatItem;

/// <summary>
/// Agent reasoning / thoughts. Render separately (italic) and optionally collapsible.
/// ThoughtId is stable across streaming updates so the UI can preserve expand/collapse state.
/// </summary>
public sealed record ChatThought(int ThoughtId, string Text) : ChatItem;

/// <summary>
/// A tool call event on the timeline. The UI layer may group consecutive tool calls with the same intent.
/// </summary>
public sealed record ChatToolCall(
    string ToolCallId,
    string Title,
    Agent.Acp.Schema.ToolCallStatus Status,
    string? RawInputJson,
    string? RawOutputJson,
    string IntentTitle) : ChatItem;

