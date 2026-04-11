using System.Collections.Concurrent;
using System.Text.Json;
using Marian.Agent.Acp.Protocol;
using Marian.Agent.Acp.Transport;

namespace Marian.Agent.Acp.Acp;

/// <summary>
/// Minimal ACP client-side helper (mainly for tests / harnesses).
/// </summary>
public sealed class AcpClientConnection : IAsyncDisposable
{
    public event Action<JsonRpcNotification>? NotificationReceived;

    /// <summary>
    /// Optional handler for incoming requests (agent->client). If set, the client connection will
    /// automatically respond.
    /// </summary>
    public Func<JsonRpcRequest, CancellationToken, Task<JsonElement>>? RequestHandler { get; set; }

    private readonly ITransport _transport;
    private long _nextId = 1;

    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _readLoop;

    public AcpClientConnection(ITransport transport)
    {
        _transport = transport;
        _readLoop = Task.Run(ReadLoopAsync);
    }

    public async Task<TResponse> RequestAsync<TRequest, TResponse>(string method, TRequest request, CancellationToken cancellationToken = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var idElement = JsonDocument.Parse(id.ToString()).RootElement.Clone();

        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending.TryAdd(id.ToString(), tcs);

        var req = new JsonRpcRequest
        {
            Id = idElement,
            Method = method,
            Params = SerializeToElement(request),
        };

        await _transport.SendMessageAsync(req, cancellationToken).ConfigureAwait(false);

        using var reg = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        var resultElement = await tcs.Task.ConfigureAwait(false);
        var result = JsonSerializer.Deserialize<TResponse>(resultElement.GetRawText(), AcpJson.Options);
        return result ?? throw new InvalidOperationException($"Failed to deserialize result as {typeof(TResponse).Name}");
    }

    private static JsonElement SerializeToElement<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, AcpJson.Options);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            await foreach (var msg in _transport.MessageReader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                switch (msg)
                {
                    case JsonRpcResponse resp:
                    {
                        var key = resp.Id.ToString();
                        if (_pending.TryRemove(key, out var tcs))
                        {
                            tcs.TrySetResult(resp.Result);
                        }
                        break;
                    }
                    case JsonRpcError err:
                    {
                        var key = err.Id.ToString();
                        if (_pending.TryRemove(key, out var tcs))
                        {
                            tcs.TrySetException(new InvalidOperationException($"JSON-RPC error {err.Error.Code}: {err.Error.Message}"));
                        }
                        break;
                    }
                    case JsonRpcNotification n:
                        NotificationReceived?.Invoke(n);
                        break;

                    case JsonRpcRequest req:
                        if (RequestHandler is not null)
                        {
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    var result = await RequestHandler(req, _cts.Token).ConfigureAwait(false);
                                    await _transport.SendMessageAsync(new JsonRpcResponse { Id = req.Id, Result = result }, _cts.Token).ConfigureAwait(false);
                                }
                                catch (Exception ex)
                                {
                                    await _transport.SendMessageAsync(new JsonRpcError
                                    {
                                        Id = req.Id,
                                        Error = new JsonRpcErrorDetail { Code = -32603, Message = ex.Message },
                                    }, _cts.Token).ConfigureAwait(false);
                                }
                            }, _cts.Token);
                        }
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _cts.Dispose();
        try { await _readLoop.ConfigureAwait(false); } catch { }
        await _transport.DisposeAsync().ConfigureAwait(false);
    }
}
