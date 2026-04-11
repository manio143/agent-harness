using Agent.Acp.Schema;

namespace Agent.Acp.Acp;

/// <summary>
/// Implement this in your agent harness. The library takes care of JSON-RPC connectivity + dispatch.
/// </summary>
public interface IAcpAgent
{
    Task<InitializeResponse> InitializeAsync(InitializeRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Optional: if your agent supports authentication.
    /// </summary>
    Task<AuthenticateResponse>? AuthenticateAsync(AuthenticateRequest request, CancellationToken cancellationToken)
        => null;

    Task<NewSessionResponse> NewSessionAsync(NewSessionRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Optional: resume an existing session (requires loadSession capability).
    /// </summary>
    Task<LoadSessionResponse>? LoadSessionAsync(LoadSessionRequest request, CancellationToken cancellationToken)
        => null;

    /// <summary>
    /// Optional: set the current session mode.
    /// </summary>
    Task<SetSessionModeResponse>? SetSessionModeAsync(SetSessionModeRequest request, CancellationToken cancellationToken)
        => null;

    Task<PromptResponse> PromptAsync(PromptRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Optional cancellation hook.
    /// </summary>
    Task CancelAsync(CancelNotification notification, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
