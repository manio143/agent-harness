using System.Collections.Immutable;

namespace Agent.Harness.Persistence;

public interface ISessionStore
{
    void CreateNew(string sessionId, SessionMetadata metadata);

    bool Exists(string sessionId);

    ImmutableArray<string> ListSessionIds();

    SessionMetadata? TryLoadMetadata(string sessionId);

    ImmutableArray<SessionEvent> LoadCommitted(string sessionId);

    void AppendCommitted(string sessionId, SessionEvent evt);

    void UpdateMetadata(string sessionId, SessionMetadata metadata);
}

public sealed record SessionMetadata(
    string SessionId,
    string Cwd,
    string? Title,
    string CreatedAtIso,
    string UpdatedAtIso);
