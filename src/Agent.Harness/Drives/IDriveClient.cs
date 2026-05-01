namespace Agent.Harness.Drives;

/// <summary>
/// Abstraction over a text-file drive.
///
/// This is intended for agent-internal code that wants to read/write files while
/// respecting the active execution context (e.g. ACP client capabilities).
/// </summary>
public interface IDriveClient
{
    Task<ReadTextResult> ReadTextAsync(string path, CancellationToken cancellationToken);
    Task WriteTextAsync(string path, string content, CancellationToken cancellationToken);
}

public sealed record ReadTextResult(string Content);
