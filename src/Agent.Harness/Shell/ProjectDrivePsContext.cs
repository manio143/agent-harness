using Agent.Acp.Acp;

namespace Agent.Harness.Shell;

/// <summary>
/// Holds the hybrid project drive context for the PowerShell runspace.
/// Content reads/writes go through ACP when available; listing/navigation uses the local session cwd.
/// </summary>
public sealed class ProjectDrivePsContext
{
    public ProjectDrivePsContext(
        string? sessionId,
        IAcpClientCaller? client,
        string? sessionCwd,
        Agent.Harness.Persistence.ISessionStore? store)
    {
        SessionId = sessionId;
        Client = client;
        SessionCwd = sessionCwd;
        Store = store;
    }

    public string? SessionId { get; }
    public IAcpClientCaller? Client { get; }
    public string? SessionCwd { get; }
    public Agent.Harness.Persistence.ISessionStore? Store { get; }

    public IAcpClientCaller RequireClient()
        => Client ?? throw new InvalidOperationException("project_drive_requires_client");

    public string ResolveProjectRoot()
    {
        var cwd = SessionCwd;
        if (string.IsNullOrWhiteSpace(cwd) && !string.IsNullOrWhiteSpace(SessionId))
            cwd = Store?.TryLoadMetadata(SessionId!)?.Cwd;

        if (string.IsNullOrWhiteSpace(cwd))
            throw new InvalidOperationException("project_cwd_unknown");

        var fullPath = Path.GetFullPath(cwd);
        if (File.Exists(fullPath))
            throw new IOException($"project_drive_root_is_file:{fullPath}");

        Directory.CreateDirectory(fullPath);
        return fullPath;
    }

    public string ResolveProjectPath(string providerPath)
    {
        var rel = NormalizeProviderPath(providerPath);
        if (string.IsNullOrWhiteSpace(rel))
            return ResolveProjectRoot();

        return NormalizeProjectRelativePath(rel);
    }

    public string NormalizeProjectRelativePath(string projectRelativePath)
    {
        if (string.IsNullOrWhiteSpace(projectRelativePath))
            throw new InvalidOperationException("project_path_required");

        var p = projectRelativePath.Replace('\\', '/');

        if (p.StartsWith("/", StringComparison.Ordinal) || p.StartsWith("~", StringComparison.Ordinal))
            throw new InvalidOperationException($"project_path_must_be_relative:{projectRelativePath}");

        if (p.Contains(':'))
            throw new InvalidOperationException($"project_path_must_be_relative:{projectRelativePath}");

        if (p.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(seg => seg == ".."))
            throw new InvalidOperationException($"project_path_traversal_not_allowed:{projectRelativePath}");

        return Path.GetFullPath(Path.Combine(ResolveProjectRoot(), projectRelativePath));
    }

    private static string NormalizeProviderPath(string? path)
        => (path ?? string.Empty).TrimStart('\\', '/');
}
