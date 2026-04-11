using System.Text.Json;
using Marian.Agent.Acp.Protocol;
using Marian.Agent.Acp.Schema;
using Marian.Agent.Acp.Transport;

namespace Marian.Agent.Acp.Acp;

/// <summary>
/// Runs an ACP-compatible agent over a JSON-RPC transport.
///
/// Current focus: stdio transport. The transport abstraction allows future custom transports.
/// </summary>
public sealed class AcpAgentServer
{
    private sealed class AgentContext : IAcpAgentContext
    {
        private readonly ITransport _transport;
        private readonly PendingRequests _pending;

        public AgentContext(string sessionId, ITransport transport, PendingRequests pending)
        {
            SessionId = sessionId;
            _transport = transport;
            _pending = pending;
        }

        public string SessionId { get; }

        public Task SendSessionUpdateAsync(object update, CancellationToken cancellationToken = default)
        {
            // We keep the payload flexible for now because the generated schema types contain
            // unions that are not fully materialized by the generator.
            var notif = new JsonRpcNotification
            {
                Method = "session/update",
                Params = SerializeToElement(new
                {
                    sessionId = SessionId,
                    update,
                }),
            };

            return _transport.SendMessageAsync(notif, cancellationToken);
        }

        public async Task<TResponse> RequestAsync<TRequest, TResponse>(string method, TRequest request, CancellationToken cancellationToken = default)
        {
            var (id, task) = _pending.Create();
            var msg = new JsonRpcRequest
            {
                Id = id,
                Method = method,
                Params = SerializeToElement(request),
            };

            await _transport.SendMessageAsync(msg, cancellationToken).ConfigureAwait(false);

            var resultElement = await task.WaitAsync(cancellationToken).ConfigureAwait(false);
            var result = JsonSerializer.Deserialize<TResponse>(resultElement.GetRawText(), AcpJson.Options);
            return result ?? throw new InvalidOperationException($"Failed to deserialize result as {typeof(TResponse).Name}");
        }
    }


    private readonly IAcpAgent _agent;
    private readonly PendingRequests _pending = new();
    private readonly Dictionary<string, CancellationTokenSource> _sessionCts = new();

    public AcpAgentServer(IAcpAgent agent)
    {
        _agent = agent;
    }

    public async Task RunAsync(ITransport transport, CancellationToken cancellationToken = default)
    {
        await foreach (var msg in transport.MessageReader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            switch (msg)
            {
                case JsonRpcRequest req:
                    _ = Task.Run(() => HandleRequestAsync(transport, req, cancellationToken), cancellationToken);
                    break;

                case JsonRpcNotification notif:
                    _ = Task.Run(() => HandleNotificationAsync(transport, notif, cancellationToken), cancellationToken);
                    break;

                case JsonRpcResponse resp:
                    _pending.TryResolve(resp.Id, resp.Result, error: null);
                    break;

                case JsonRpcError err:
                    _pending.TryResolve(err.Id, result: null, error: new InvalidOperationException($"JSON-RPC error {err.Error.Code}: {err.Error.Message}"));
                    break;
            }
        }
    }

    private async Task HandleNotificationAsync(ITransport transport, JsonRpcNotification notif, CancellationToken cancellationToken)
    {
        // Session cancellation.
        if (notif.Method == "session/cancel" && notif.Params is { } p)
        {
            var cancel = Deserialize<CancelNotification>(p);

            lock (_sessionCts)
            {
                if (_sessionCts.TryGetValue(cancel.SessionId, out var cts))
                {
                    cts.Cancel();
                }
            }

            await _agent.CancelAsync(cancel, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Ignore other notifications for now.
    }

    private async Task HandleRequestAsync(ITransport transport, JsonRpcRequest req, CancellationToken cancellationToken)
    {
        try
        {
            switch (req.Method)
            {
                case "initialize":
                {
                    var init = Deserialize<InitializeRequest>(req.Params);
                    var result = await _agent.InitializeAsync(init, cancellationToken).ConfigureAwait(false);
                    await transport.SendMessageAsync(new JsonRpcResponse { Id = req.Id, Result = SerializeToElement(result) }, cancellationToken);
                    break;
                }

                case "authenticate":
                {
                    var authReq = Deserialize<AuthenticateRequest>(req.Params);
                    var auth = _agent.AuthenticateAsync(authReq, cancellationToken);
                    if (auth is null)
                    {
                        await transport.SendMessageAsync(new JsonRpcError
                        {
                            Id = req.Id,
                            Error = new JsonRpcErrorDetail { Code = -32601, Message = "authenticate not supported" },
                        }, cancellationToken);
                        break;
                    }

                    var result = await auth.ConfigureAwait(false);
                    await transport.SendMessageAsync(new JsonRpcResponse { Id = req.Id, Result = SerializeToElement(result) }, cancellationToken);
                    break;
                }

                case "session/new":
                {
                    var newSession = Deserialize<NewSessionRequest>(req.Params);
                    var result = await _agent.NewSessionAsync(newSession, cancellationToken).ConfigureAwait(false);

                    // Create CTS for future prompt cancellation (best-effort; sessionId comes from response).
                    if (!string.IsNullOrWhiteSpace(result.SessionId))
                    {
                        lock (_sessionCts)
                        {
                            _sessionCts[result.SessionId] = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        }
                    }

                    await transport.SendMessageAsync(new JsonRpcResponse { Id = req.Id, Result = SerializeToElement(result) }, cancellationToken);
                    break;
                }

                case "session/prompt":
                {
                    var prompt = Deserialize<PromptRequest>(req.Params);

                    CancellationToken promptToken = cancellationToken;
                    lock (_sessionCts)
                    {
                        if (_sessionCts.TryGetValue(prompt.SessionId, out var cts))
                        {
                            promptToken = cts.Token;
                        }
                    }

                    PromptResponse result;
                    if (_agent is IAcpAgentWithContext agentWithCtx)
                    {
                        var ctx = new AgentContext(prompt.SessionId, transport, _pending);
                        result = await agentWithCtx.PromptAsync(prompt, ctx, promptToken).ConfigureAwait(false);
                    }
                    else
                    {
                        result = await _agent.PromptAsync(prompt, promptToken).ConfigureAwait(false);
                    }

                    await transport.SendMessageAsync(new JsonRpcResponse { Id = req.Id, Result = SerializeToElement(result) }, cancellationToken);
                    break;
                }

                default:
                    await transport.SendMessageAsync(new JsonRpcError
                    {
                        Id = req.Id,
                        Error = new JsonRpcErrorDetail { Code = -32601, Message = $"Method not found: {req.Method}" },
                    }, cancellationToken);
                    break;
            }
        }
        catch (Exception ex)
        {
            await transport.SendMessageAsync(new JsonRpcError
            {
                Id = req.Id,
                Error = new JsonRpcErrorDetail { Code = -32603, Message = ex.Message },
            }, cancellationToken);
        }
    }

    private static T Deserialize<T>(JsonElement? element)
    {
        if (element is null)
        {
            throw new InvalidOperationException("Missing JSON-RPC params.");
        }

        return JsonSerializer.Deserialize<T>(element.Value.GetRawText(), AcpJson.Options)
               ?? throw new InvalidOperationException($"Failed to deserialize params as {typeof(T).Name}.");
    }

    private static JsonElement SerializeToElement<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, AcpJson.Options);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
