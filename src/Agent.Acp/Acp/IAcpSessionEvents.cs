namespace Agent.Acp.Acp;

/// <summary>
/// Session-scoped side-channel for agent -> client notifications (e.g. session/update).
/// </summary>
public interface IAcpSessionEvents
{
    Task SendSessionUpdateAsync(object update, CancellationToken cancellationToken = default);
}
