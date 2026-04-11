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
    private readonly IAcpAgent _agent;

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
                    _ = Task.Run(() => HandleNotificationAsync(notif, cancellationToken), cancellationToken);
                    break;
            }
        }
    }

    private async Task HandleNotificationAsync(JsonRpcNotification notif, CancellationToken cancellationToken)
    {
        // For now we only wire cancellation.
        if (notif.Method == "session/cancel" && notif.Params is { } p)
        {
            var cancel = Deserialize<CancelNotification>(p);
            await _agent.CancelAsync(cancel, cancellationToken).ConfigureAwait(false);
        }
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
                    await transport.SendMessageAsync(new JsonRpcResponse { Id = req.Id, Result = SerializeToElement(result) }, cancellationToken);
                    break;
                }

                case "session/prompt":
                {
                    var prompt = Deserialize<PromptRequest>(req.Params);
                    var result = await _agent.PromptAsync(prompt, cancellationToken).ConfigureAwait(false);
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
