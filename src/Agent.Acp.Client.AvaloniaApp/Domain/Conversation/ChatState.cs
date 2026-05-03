using System.Collections.Immutable;

namespace Agent.Acp.Client.AvaloniaApp.Domain.Conversation;

public sealed record ChatState(
    ImmutableArray<ChatItem> Items,
    string? CurrentIntent,
    ImmutableDictionary<string, ChatToolCall> ToolCallsById,
    LastChunkKind LastChunk)
{
    public static ChatState Empty { get; } = new(
        Items: ImmutableArray<ChatItem>.Empty,
        CurrentIntent: null,
        ToolCallsById: ImmutableDictionary<string, ChatToolCall>.Empty,
        LastChunk: LastChunkKind.None);
}

public enum LastChunkKind
{
    None = 0,
    AgentMessage = 1,
    AgentThought = 2,
}

