using Agent.Acp.Schema;

namespace Agent.Acp.Acp;

/// <summary>
/// Session-scoped agent implementation. Instances are created by <see cref="IAcpAgentFactory"/>
/// after a session is created or loaded.
/// </summary>
public interface IAcpSessionAgent
{
    Task<PromptResponse> PromptAsync(PromptRequest request, IAcpPromptTurn turn, CancellationToken cancellationToken);

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

    /// <summary>
    /// Optional: set a session config option.
    /// </summary>
    Task<SetSessionConfigOptionResponse>? SetSessionConfigOptionAsync(SetSessionConfigOptionRequest request, CancellationToken cancellationToken)
        => null;
}
