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

    Task<PromptResponse> PromptAsync(PromptRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Optional cancellation hook.
    /// </summary>
    Task CancelAsync(CancelNotification notification, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
