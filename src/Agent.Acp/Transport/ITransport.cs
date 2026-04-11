using System.Threading.Channels;
using Agent.Acp.Protocol;

namespace Agent.Acp.Transport;

/// <summary>
/// Transport abstraction for ACP over JSON-RPC.
///
/// This is inspired by the MCP C# SDK's transport abstraction: a sender + a channel-based receiver.
/// </summary>
public interface ITransport : IAsyncDisposable
{
    string Name { get; }

    ChannelReader<JsonRpcMessage> MessageReader { get; }

    Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default);
}
