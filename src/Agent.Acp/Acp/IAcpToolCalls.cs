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

    /// <summary>
    /// Append content to the tool call. Valid only while the call is pending or in-progress.
    /// </summary>
    Task AddContentAsync(ToolCallContent content, CancellationToken cancellationToken = default);

    Task InProgressAsync(CancellationToken cancellationToken = default);

    Task CompletedAsync(CancellationToken cancellationToken = default, object? rawOutput = null);

    Task FailedAsync(string message, CancellationToken cancellationToken = default);

    Task CancelledAsync(CancellationToken cancellationToken = default);
}
