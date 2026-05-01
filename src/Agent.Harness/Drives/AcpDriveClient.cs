using Agent.Acp.Acp;

namespace Agent.Harness.Drives;

/// <summary>
/// Drive client backed by ACP fs/read_text_file + fs/write_text_file.
///
/// IMPORTANT:
/// - This is executed in the context of the ACP client (remote filesystem), not the agent host.
/// - Capability checks are enforced (throws if missing).
/// </summary>
public sealed class AcpDriveClient : IDriveClient
{
    private readonly string _sessionId;
    private readonly Agent.Acp.Acp.IAcpClientCaller _client;
    private readonly string? _sessionCwd;
    private readonly Agent.Harness.Persistence.ISessionStore? _store;

    public AcpDriveClient(
        string sessionId,
        Agent.Acp.Acp.IAcpClientCaller client,
        string? sessionCwd = null,
        Agent.Harness.Persistence.ISessionStore? store = null)
    {
        _sessionId = sessionId;
        _client = client;
        _sessionCwd = sessionCwd;
        _store = store;
    }

    public async Task<ReadTextResult> ReadTextAsync(string path, CancellationToken cancellationToken)
    {
        var normalized = NormalizeFsPath(path);

        var resp = await _client.ReadTextFileAsync(new Agent.Acp.Schema.ReadTextFileRequest
        {
            SessionId = _sessionId,
            Path = normalized,
        }, cancellationToken).ConfigureAwait(false);

        return new ReadTextResult(resp.Content);
    }

    public async Task WriteTextAsync(string path, string content, CancellationToken cancellationToken)
    {
        var normalized = NormalizeFsPath(path);

        await _client.WriteTextFileAsync(new Agent.Acp.Schema.WriteTextFileRequest
        {
            SessionId = _sessionId,
            Path = normalized,
            Content = content,
        }, cancellationToken).ConfigureAwait(false);
    }

    private string NormalizeFsPath(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return rawPath;

        var cwd = _sessionCwd;
        if (string.IsNullOrWhiteSpace(cwd))
            cwd = _store?.TryLoadMetadata(_sessionId)?.Cwd;

        if (string.IsNullOrWhiteSpace(cwd))
            return Path.GetFullPath(rawPath);

        return Path.IsPathRooted(rawPath)
            ? Path.GetFullPath(rawPath)
            : Path.GetFullPath(Path.Combine(cwd, rawPath));
    }
}
