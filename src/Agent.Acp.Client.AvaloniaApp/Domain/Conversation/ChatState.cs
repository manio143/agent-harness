using System.Collections.Immutable;

namespace Agent.Acp.Client.AvaloniaApp.Domain.Conversation;

public sealed record ChatState(
    ImmutableArray<ChatItem> Items,
    string? CurrentIntent,
    ImmutableDictionary<string, ChatToolCall> ToolCallsById,
    bool LastWasAgentMessageChunk)
{
    public static ChatState Empty { get; } = new(
        Items: ImmutableArray<ChatItem>.Empty,
        CurrentIntent: null,
        ToolCallsById: ImmutableDictionary<string, ChatToolCall>.Empty,
        LastWasAgentMessageChunk: false);
}
