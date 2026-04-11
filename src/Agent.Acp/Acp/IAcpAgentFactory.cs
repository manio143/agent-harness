using Agent.Acp.Schema;

namespace Agent.Acp.Acp;

/// <summary>
/// Entry-point for building an ACP agent.
/// Handles global methods (initialize/authenticate/session creation) and produces per-session agents.
/// </summary>
public interface IAcpAgentFactory
{
    Task<InitializeResponse> InitializeAsync(InitializeRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Optional: if your agent supports authentication.
    /// </summary>
    Task<AuthenticateResponse>? AuthenticateAsync(AuthenticateRequest request, CancellationToken cancellationToken)
        => null;

    Task<NewSessionResponse> NewSessionAsync(NewSessionRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Optional: list existing sessions.
    /// Only available if initialize response advertises agentCapabilities.sessionCapabilities.list.
    /// </summary>
    Task<ListSessionsResponse>? ListSessionsAsync(ListSessionsRequest request, CancellationToken cancellationToken)
        => null;

    /// <summary>
    /// Optional: resume an existing session (requires loadSession capability).
    /// </summary>
    Task<LoadSessionResponse>? LoadSessionAsync(LoadSessionRequest request, CancellationToken cancellationToken)
        => null;

    /// <summary>
    /// Create the session-scoped agent that will handle prompt turns.
    /// </summary>
    IAcpSessionAgent CreateSessionAgent(string sessionId, IAcpClientCaller client, IAcpSessionEvents events);
}
