using Agent.Acp.Schema;

namespace Agent.Acp.Acp;

public static class AcpTerminalClientExtensions
{
    public static Task<CreateTerminalResponse> CreateTerminalAsync(this IAcpClientCaller client, CreateTerminalRequest request, CancellationToken cancellationToken = default)
    {
        EnsureSupported(client);
        return client.RequestAsync<CreateTerminalRequest, CreateTerminalResponse>("terminal/create", request, cancellationToken);
    }

    public static Task<TerminalOutputResponse> GetTerminalOutputAsync(this IAcpClientCaller client, TerminalOutputRequest request, CancellationToken cancellationToken = default)
    {
        EnsureSupported(client);
        return client.RequestAsync<TerminalOutputRequest, TerminalOutputResponse>("terminal/output", request, cancellationToken);
    }

    public static Task<WaitForTerminalExitResponse> WaitForTerminalExitAsync(this IAcpClientCaller client, WaitForTerminalExitRequest request, CancellationToken cancellationToken = default)
    {
        EnsureSupported(client);
        return client.RequestAsync<WaitForTerminalExitRequest, WaitForTerminalExitResponse>("terminal/wait_for_exit", request, cancellationToken);
    }

    public static Task<object?> KillTerminalAsync(this IAcpClientCaller client, KillTerminalRequest request, CancellationToken cancellationToken = default)
    {
        EnsureSupported(client);
        return client.RequestAsync<KillTerminalRequest, object?>("terminal/kill", request, cancellationToken);
    }

    public static Task<object?> ReleaseTerminalAsync(this IAcpClientCaller client, ReleaseTerminalRequest request, CancellationToken cancellationToken = default)
    {
        EnsureSupported(client);
        return client.RequestAsync<ReleaseTerminalRequest, object?>("terminal/release", request, cancellationToken);
    }

    private static void EnsureSupported(IAcpClientCaller client)
    {
        if (client.ClientCapabilities.Terminal != true)
            throw new InvalidOperationException("Client did not advertise terminal capability");
    }
}
