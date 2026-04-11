using System.Threading.Channels;
using Agent.Acp.Protocol;

namespace Agent.Acp.Transport;

public abstract class TransportBase : ITransport
{
    private readonly Channel<JsonRpcMessage> _channel;

    protected TransportBase(string name)
    {
        Name = name;
        _channel = Channel.CreateUnbounded<JsonRpcMessage>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public string Name { get; }

    public ChannelReader<JsonRpcMessage> MessageReader => _channel.Reader;

    protected bool TryWriteIncoming(JsonRpcMessage message) => _channel.Writer.TryWrite(message);

    protected void CompleteIncoming(Exception? error = null) => _channel.Writer.TryComplete(error);

    public abstract Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default);

    public abstract ValueTask DisposeAsync();
}
