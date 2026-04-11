using System.Text;
using System.Text.Json;
using Marian.Agent.Acp.Protocol;

namespace Marian.Agent.Acp.Transport;

/// <summary>
/// A very small JSON-RPC transport over streams using newline-delimited JSON (one message per line).
///
/// Intended for ACP's current stdio transport.
/// </summary>
public sealed class LineDelimitedStreamTransport : TransportBase
{
    private static readonly byte[] NewlineBytes = "\n"u8.ToArray();

    private readonly TextReader _reader;
    private readonly Stream _writer;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _readLoop;

    public LineDelimitedStreamTransport(Stream input, Stream output, string name = "stdio")
        : base(name)
    {
        _reader = new StreamReader(input, Encoding.UTF8);
        _writer = output;

        _readLoop = Task.Run(ReadLoopAsync);
    }

    public override async Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(message, AcpJson.Options);

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _writer.WriteAsync(json, cancellationToken).ConfigureAwait(false);
            await _writer.WriteAsync(NewlineBytes, cancellationToken).ConfigureAwait(false);
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        Exception? error = null;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var line = await _reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    var msg = JsonSerializer.Deserialize<JsonRpcMessage>(line, AcpJson.Options);
                    if (msg is not null)
                    {
                        TryWriteIncoming(msg);
                    }
                }
                catch (JsonException)
                {
                    // Ignore malformed lines, keep reading.
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            error = ex;
        }
        finally
        {
            CompleteIncoming(error);
        }
    }

    public override async ValueTask DisposeAsync()
    {
        try
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }
        catch
        {
            // ignore
        }

        _cts.Dispose();
        _reader.Dispose();
        await _writer.DisposeAsync().ConfigureAwait(false);

        try { await _readLoop.ConfigureAwait(false); } catch { }

        CompleteIncoming();
    }
}
