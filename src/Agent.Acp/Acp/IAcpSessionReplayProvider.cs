using Agent.Acp.Schema;

namespace Agent.Acp.Acp;

/// <summary>
/// Optional extension interface for agents that can replay session history during session/load.
///
/// ACP requires that, when loading a session, the agent replays the entire conversation via
/// session/update notifications before completing the session/load response.
/// </summary>
public interface IAcpSessionReplayProvider
{
    /// <summary>
    /// Replay the loaded session's conversation history via <paramref name="events"/>.
    /// Implementations MUST only emit session/update notifications (no final response).
    /// </summary>
    Task ReplaySessionAsync(string sessionId, IAcpSessionEvents events, CancellationToken cancellationToken);
}
