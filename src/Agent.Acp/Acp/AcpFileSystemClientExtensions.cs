using Agent.Acp.Schema;

namespace Agent.Acp.Acp;

public static class AcpFileSystemClientExtensions
{
    public static Task<ReadTextFileResponse> ReadTextFileAsync(this IAcpClientCaller client, ReadTextFileRequest request, CancellationToken cancellationToken = default)
    {
        var caps = GetCaps(client);
        if (caps.Fs is null || caps.Fs.ReadTextFile != true)
            throw new InvalidOperationException("Client did not advertise fs.readTextFile capability");

        return client.RequestAsync<ReadTextFileRequest, ReadTextFileResponse>("fs/read_text_file", request, cancellationToken);
    }

    public static Task<object?> WriteTextFileAsync(this IAcpClientCaller client, WriteTextFileRequest request, CancellationToken cancellationToken = default)
    {
        var caps = GetCaps(client);
        if (caps.Fs is null || caps.Fs.WriteTextFile != true)
            throw new InvalidOperationException("Client did not advertise fs.writeTextFile capability");

        // ACP returns null result for write
        return client.RequestAsync<WriteTextFileRequest, object?>("fs/write_text_file", request, cancellationToken);
    }

    private static ClientCapabilities GetCaps(IAcpClientCaller client)
    {
        return client.ClientCapabilities;
    }
}
