using Agent.Acp.Schema;

namespace Agent.Acp.Acp;

/// <summary>
/// Session-scoped agent implementation. Instances are created by <see cref="IAcpAgentFactory"/>
/// after a session is created or loaded.
/// </summary>
public interface IAcpSessionAgent
{
    Task<PromptResponse> PromptAsync(PromptRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Optional cancellation hook.
    /// </summary>
    Task CancelAsync(CancelNotification notification, CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <summary>
    /// Optional: set the current session mode.
    /// </summary>
    Task<SetSessionModeResponse>? SetSessionModeAsync(SetSessionModeRequest request, CancellationToken cancellationToken)
        => null;
}
