using Agent.Acp.Acp;

namespace Agent.Harness.Shell;

/// <summary>
/// Holds the ACP client context for the PowerShell runspace.
/// Stored in the runspace SessionState as a variable.
/// </summary>
public sealed class AcpClientPsContext
{
    public AcpClientPsContext(
        string sessionId,
        IAcpClientCaller client,
        string? sessionCwd,
        Agent.Harness.Persistence.ISessionStore? store)
    {
        SessionId = sessionId;
        Client = client;
        SessionCwd = sessionCwd;
        Store = store;
    }

    public string SessionId { get; }
    public IAcpClientCaller Client { get; }

    /// <summary>
    /// When relative paths are provided for client drive operations, they are rooted under SessionCwd.
    /// </summary>
    public string? SessionCwd { get; }

    public Agent.Harness.Persistence.ISessionStore? Store { get; }

    public string NormalizeClientRelativePath(string clientRelativePath)
    {
        // Disallow embedded drive roots like "C:\..." or "Env:\...".
        // This drive is anchored at the ACP session cwd.
        if (string.IsNullOrWhiteSpace(clientRelativePath))
            throw new InvalidOperationException("client_path_required");

        // Normalize to forward slashes for checks.
        var p = clientRelativePath.Replace('\\', '/');

        // Reject anything that looks like an absolute path or contains a ':' (drive/provider).
        if (p.StartsWith("/", StringComparison.Ordinal) || p.StartsWith("~", StringComparison.Ordinal))
            throw new InvalidOperationException($"client_path_must_be_relative:{clientRelativePath}");

        if (p.Contains(':'))
            throw new InvalidOperationException($"client_path_must_be_relative:{clientRelativePath}");

        // Reject traversal.
        if (p.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(seg => seg == ".."))
            throw new InvalidOperationException($"client_path_traversal_not_allowed:{clientRelativePath}");

        var cwd = SessionCwd;
        if (string.IsNullOrWhiteSpace(cwd))
            cwd = Store?.TryLoadMetadata(SessionId)?.Cwd;

        if (string.IsNullOrWhiteSpace(cwd))
            throw new InvalidOperationException("client_cwd_unknown");

        // Keep behavior consistent with existing fs tool normalization.
        return Path.GetFullPath(Path.Combine(cwd, clientRelativePath));
    }
}
