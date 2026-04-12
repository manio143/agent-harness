using System.Text.Json;
using System.Threading.Channels;
using Agent.Acp.Protocol;
using Agent.Acp.Transport;

namespace Agent.Server;

/// <summary>
/// Debug transport wrapper that logs JSON-RPC messages to STDERR.
///
/// WARNING: this is a developer aid. It can include user content.
/// Keep it off by default.
/// </summary>
public sealed class RpcLoggingTransport : ITransport
{
    private readonly ITransport _inner;
    private readonly Channel<JsonRpcMessage> _channel;
    private readonly Task _pump;

    public RpcLoggingTransport(ITransport inner)
    {
        _inner = inner;
        _channel = Channel.CreateUnbounded<JsonRpcMessage>(new UnboundedChannelOptions
        {
            AllowSynchronousContinuations = false,
            SingleReader = false,
            SingleWriter = true,
        });

        _pump = Task.Run(PumpAsync);
    }

    public string Name => _inner.Name + "+rpc-log";

    public ChannelReader<JsonRpcMessage> MessageReader => _channel.Reader;

    public async Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
    {
        Log("S->C", message);
        await _inner.SendMessageAsync(message, cancellationToken).ConfigureAwait(false);
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var msg in _inner.MessageReader.ReadAllAsync().ConfigureAwait(false))
            {
                Log("C->S", msg);
                await _channel.Writer.WriteAsync(msg).ConfigureAwait(false);
            }

            _channel.Writer.TryComplete();
        }
        catch (Exception ex)
        {
            _channel.Writer.TryComplete(ex);
        }
    }

    private static void Log(string dir, JsonRpcMessage msg)
    {
        try
        {
            var json = JsonSerializer.Serialize(msg, new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = false,
            });

            Console.Error.WriteLine($"[rpc {dir}] {json}");
        }
        catch
        {
            // best-effort
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);

        try { await _pump.ConfigureAwait(false); } catch { }
    }
}
