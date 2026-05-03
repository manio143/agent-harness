using System.Collections.Generic;

namespace Agent.Acp.Client.AvaloniaApp.Domain.Conversation;

public abstract record ChatItem;

public sealed record ChatText(string Text) : ChatItem;

public sealed record ChatIntentGroup(string Title, IReadOnlyList<ChatToolCall> ToolCalls) : ChatItem;

public sealed record ChatToolCall(string ToolCallId, string Title, Agent.Acp.Schema.ToolCallStatus Status);
