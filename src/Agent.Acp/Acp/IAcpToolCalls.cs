using Agent.Acp.Schema;

namespace Agent.Acp.Acp;

public interface IAcpToolCalls
{
    IAcpToolCall Start(string toolCallId, string title, ToolKind kind);

    IReadOnlyCollection<string> ActiveToolCallIds { get; }

    Task CancelAllAsync(CancellationToken cancellationToken = default);
}

public interface IAcpToolCall
{
    string ToolCallId { get; }

    Task InProgressAsync(CancellationToken cancellationToken = default);

    Task CompletedAsync(IReadOnlyList<ToolCallContent> content, CancellationToken cancellationToken = default);

    Task CancelledAsync(CancellationToken cancellationToken = default);
}
