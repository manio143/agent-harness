using System.Threading.Channels;
using Agent.Acp.Protocol;
using Agent.Acp.Transport;

namespace Agent.Acp.Tests;

/// <summary>
/// Two in-memory transports connected back-to-back for integration tests.
/// </summary>
public sealed class InMemoryTransport : ITransport
{
    private readonly ChannelReader<JsonRpcMessage> _reader;
    private readonly ChannelWriter<JsonRpcMessage> _writer;

    public InMemoryTransport(string name, ChannelReader<JsonRpcMessage> reader, ChannelWriter<JsonRpcMessage> writer)
    {
        Name = name;
        _reader = reader;
        _writer = writer;
    }

    public string Name { get; }

    public ChannelReader<JsonRpcMessage> MessageReader => _reader;

    public Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
    {
        _writer.TryWrite(message);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public static (InMemoryTransport client, InMemoryTransport server) CreatePair()
    {
        var clientToServer = Channel.CreateUnbounded<JsonRpcMessage>();
        var serverToClient = Channel.CreateUnbounded<JsonRpcMessage>();

        var client = new InMemoryTransport("client", serverToClient.Reader, clientToServer.Writer);
        var server = new InMemoryTransport("server", clientToServer.Reader, serverToClient.Writer);

        return (client, server);
    }
}
