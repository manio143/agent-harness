using System.Text.Json;
using Agent.Acp.Protocol;
using Agent.Acp.Schema;
using Agent.Acp.Transport;

namespace Agent.Acp.Acp;

/// <summary>
/// Runs an ACP-compatible agent over a JSON-RPC transport.
///
/// Current focus: stdio transport. The transport abstraction allows future custom transports.
/// </summary>
public sealed class AcpAgentServer
{
    private sealed class AgentContext : IAcpClientCaller, IAcpSessionEvents
    {
        private readonly ITransport _transport;
        private readonly PendingRequests _pending;

        public AgentContext(string sessionId, ITransport transport, PendingRequests pending, ClientCapabilities clientCapabilities)
        {
            SessionId = sessionId;
            _transport = transport;
            _pending = pending;
            ClientCapabilities = clientCapabilities;
        }

        public string SessionId { get; }

        public ClientCapabilities ClientCapabilities { get; }

        public Task SendSessionUpdateAsync(object update, CancellationToken cancellationToken = default)
        {
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


    private sealed class SessionAgentHandle
    {
        public SessionAgentHandle(IAcpSessionAgent agent, CancellationTokenSource cts)
        {
            Agent = agent;
            Cts = cts;
        }

        public IAcpSessionAgent Agent { get; }
        public CancellationTokenSource Cts { get; }
    }

    private readonly IAcpAgentFactory _factory;
    private readonly PendingRequests _pending = new();
    private readonly Dictionary<string, SessionAgentHandle> _sessions = new();

    private bool _initialized;
    private AgentCapabilities? _agentCapabilities;
    private ClientCapabilities? _clientCapabilities;

    private readonly int _supportedProtocolVersion;

    public AcpAgentServer(IAcpAgentFactory factory, int supportedProtocolVersion = 1)
    {
        _factory = factory;
        _supportedProtocolVersion = supportedProtocolVersion;
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

            SessionAgentHandle? handle;
            lock (_sessions)
            {
                _sessions.TryGetValue(cancel.SessionId, out handle);
            }

            handle?.Cts.Cancel();

            if (handle is not null)
            {
                await handle.Agent.CancelAsync(cancel, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        // Ignore other notifications for now.
    }

    private async Task HandleRequestAsync(ITransport transport, JsonRpcRequest req, CancellationToken cancellationToken)
    {
        try
        {
            if (!_initialized && req.Method != "initialize")
            {
                await transport.SendMessageAsync(new JsonRpcError
                {
                    Id = req.Id,
                    Error = new JsonRpcErrorDetail { Code = AcpErrors.NotInitialized, Message = "Connection not initialized. Call initialize first." },
                }, cancellationToken);
                return;
            }

            switch (req.Method)
            {
                case "initialize":
                {
                    var init = Deserialize<InitializeRequest>(req.Params);
                    var result = await _factory.InitializeAsync(init, cancellationToken).ConfigureAwait(false);

                    // Version negotiation (per docs): if requested is supported, echo it; otherwise respond with latest supported.
                    result.ProtocolVersion = init.ProtocolVersion == _supportedProtocolVersion
                        ? init.ProtocolVersion
                        : _supportedProtocolVersion;

                    _initialized = true;
                    _agentCapabilities = result.AgentCapabilities;
                    _clientCapabilities = init.ClientCapabilities ?? new ClientCapabilities();

                    await transport.SendMessageAsync(new JsonRpcResponse { Id = req.Id, Result = SerializeToElement(result) }, cancellationToken);
                    break;
                }

                case "authenticate":
                {
                    var authReq = Deserialize<AuthenticateRequest>(req.Params);
                    var auth = _factory.AuthenticateAsync(authReq, cancellationToken);
                    if (auth is null)
                    {
                        await transport.SendMessageAsync(new JsonRpcError
                        {
                            Id = req.Id,
                            Error = new JsonRpcErrorDetail { Code = AcpErrors.MethodNotFound, Message = "authenticate not supported" },
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
                    if (!Path.IsPathRooted(newSession.Cwd))
                    {
                        await transport.SendMessageAsync(new JsonRpcError
                        {
                            Id = req.Id,
                            Error = new JsonRpcErrorDetail { Code = AcpErrors.InvalidParams, Message = "cwd must be an absolute path" },
                        }, cancellationToken);
                        break;
                    }

                    var result = await _factory.NewSessionAsync(newSession, cancellationToken).ConfigureAwait(false);

                    if (!string.IsNullOrWhiteSpace(result.SessionId))
                    {
                        var sessionId = result.SessionId;
                        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                        // Build caller + events for this session and create the session agent.
                        var clientCaller = new AgentContext(sessionId, transport, _pending, _clientCapabilities ?? new ClientCapabilities());
                        var sessionAgent = _factory.CreateSessionAgent(sessionId, clientCaller, clientCaller);

                        lock (_sessions)
                        {
                            _sessions[sessionId] = new SessionAgentHandle(sessionAgent, cts);
                        }
                    }

                    await transport.SendMessageAsync(new JsonRpcResponse { Id = req.Id, Result = SerializeToElement(result) }, cancellationToken);
                    break;
                }

                case "session/load":
                {
                    var loadReq = Deserialize<LoadSessionRequest>(req.Params);

                    if (_agentCapabilities?.LoadSession != true)
                    {
                        await transport.SendMessageAsync(new JsonRpcError
                        {
                            Id = req.Id,
                            Error = new JsonRpcErrorDetail { Code = AcpErrors.InvalidParams, Message = "Agent did not advertise loadSession capability" },
                        }, cancellationToken);
                        break;
                    }

                    if (!Path.IsPathRooted(loadReq.Cwd))
                    {
                        await transport.SendMessageAsync(new JsonRpcError
                        {
                            Id = req.Id,
                            Error = new JsonRpcErrorDetail { Code = AcpErrors.InvalidParams, Message = "cwd must be an absolute path" },
                        }, cancellationToken);
                        break;
                    }

                    var load = _factory.LoadSessionAsync(loadReq, cancellationToken);
                    if (load is null)
                    {
                        await transport.SendMessageAsync(new JsonRpcError
                        {
                            Id = req.Id,
                            Error = new JsonRpcErrorDetail { Code = AcpErrors.MethodNotFound, Message = "session/load not supported" },
                        }, cancellationToken);
                        break;
                    }

                    var result = await load.ConfigureAwait(false);

                    if (!string.IsNullOrWhiteSpace(loadReq.SessionId))
                    {
                        var sessionId = loadReq.SessionId;
                        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                        var clientCaller = new AgentContext(sessionId, transport, _pending, _clientCapabilities ?? new ClientCapabilities());
                        var sessionAgent = _factory.CreateSessionAgent(sessionId, clientCaller, clientCaller);

                        lock (_sessions)
                        {
                            _sessions[sessionId] = new SessionAgentHandle(sessionAgent, cts);
                        }
                    }

                    await transport.SendMessageAsync(new JsonRpcResponse { Id = req.Id, Result = SerializeToElement(result) }, cancellationToken);
                    break;
                }

                case "session/set_mode":
                {
                    var setModeReq = Deserialize<SetSessionModeRequest>(req.Params);

                    SessionAgentHandle? handle;
                    lock (_sessions)
                    {
                        _sessions.TryGetValue(setModeReq.SessionId, out handle);
                    }

                    var setMode = handle?.Agent.SetSessionModeAsync(setModeReq, cancellationToken);
                    if (setMode is null)
                    {
                        await transport.SendMessageAsync(new JsonRpcError
                        {
                            Id = req.Id,
                            Error = new JsonRpcErrorDetail { Code = AcpErrors.MethodNotFound, Message = "session/set_mode not supported" },
                        }, cancellationToken);
                        break;
                    }

                    var result = await setMode.ConfigureAwait(false);
                    await transport.SendMessageAsync(new JsonRpcResponse { Id = req.Id, Result = SerializeToElement(result) }, cancellationToken);
                    break;
                }

                case "session/set_config_option":
                {
                    var setReq = Deserialize<SetSessionConfigOptionRequest>(req.Params);

                    SessionAgentHandle? handle;
                    lock (_sessions)
                    {
                        _sessions.TryGetValue(setReq.SessionId, out handle);
                    }

                    var set = handle?.Agent.SetSessionConfigOptionAsync(setReq, cancellationToken);
                    if (set is null)
                    {
                        await transport.SendMessageAsync(new JsonRpcError
                        {
                            Id = req.Id,
                            Error = new JsonRpcErrorDetail { Code = AcpErrors.MethodNotFound, Message = "session/set_config_option not supported" },
                        }, cancellationToken);
                        break;
                    }

                    var result = await set.ConfigureAwait(false);
                    if (result.ConfigOptions is null || result.ConfigOptions.Count == 0)
                    {
                        await transport.SendMessageAsync(new JsonRpcError
                        {
                            Id = req.Id,
                            Error = new JsonRpcErrorDetail { Code = AcpErrors.InvalidParams, Message = "SetSessionConfigOptionResponse.configOptions is required" },
                        }, cancellationToken);
                        break;
                    }

                    await transport.SendMessageAsync(new JsonRpcResponse { Id = req.Id, Result = SerializeToElement(result) }, cancellationToken);
                    break;
                }

                case "session/prompt":
                {
                    var prompt = Deserialize<PromptRequest>(req.Params);

                    SessionAgentHandle? handle;
                    lock (_sessions)
                    {
                        _sessions.TryGetValue(prompt.SessionId, out handle);
                    }

                    if (handle is null)
                    {
                        await transport.SendMessageAsync(new JsonRpcError
                        {
                            Id = req.Id,
                            Error = new JsonRpcErrorDetail { Code = AcpErrors.InvalidParams, Message = $"Unknown session: {prompt.SessionId}" },
                        }, cancellationToken);
                        break;
                    }

                    if (!ValidatePromptCapabilities(prompt, _agentCapabilities, out var errorMessage))
                    {
                        await transport.SendMessageAsync(new JsonRpcError
                        {
                            Id = req.Id,
                            Error = new JsonRpcErrorDetail { Code = AcpErrors.InvalidParams, Message = errorMessage },
                        }, cancellationToken);
                        break;
                    }

                    var toolCalls = new AcpToolCallTracker(new AgentContext(prompt.SessionId, transport, _pending, _clientCapabilities ?? new ClientCapabilities()));
                    var turn = new AcpPromptTurn(toolCalls);

                    PromptResponse result;
                    try
                    {
                        result = await handle.Agent.PromptAsync(prompt, turn, handle.Cts.Token).ConfigureAwait(false);

                        // Spec: if tool calls are still active, agent must not end the turn.
                        if (toolCalls.ActiveToolCallIds.Count > 0)
                            throw new InvalidOperationException("Cannot complete prompt while tool calls are still active.");
                    }
                    catch (OperationCanceledException)
                    {
                        // Per docs: cancellation is not an error; must return StopReason=cancelled.
                        await toolCalls.CancelAllAsync(cancellationToken).ConfigureAwait(false);
                        result = new PromptResponse { StopReason = StopReason.Cancelled };
                    }

                    if (string.IsNullOrWhiteSpace(result.StopReason.Value))
                    {
                        await transport.SendMessageAsync(new JsonRpcError
                        {
                            Id = req.Id,
                            Error = new JsonRpcErrorDetail { Code = AcpErrors.InvalidParams, Message = "PromptResponse.stopReason is required" },
                        }, cancellationToken);
                        break;
                    }

                    await transport.SendMessageAsync(new JsonRpcResponse { Id = req.Id, Result = SerializeToElement(result) }, cancellationToken);
                    break;
                }

                default:
                    await transport.SendMessageAsync(new JsonRpcError
                    {
                        Id = req.Id,
                        Error = new JsonRpcErrorDetail { Code = AcpErrors.MethodNotFound, Message = $"Method not found: {req.Method}" },
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

    private static bool ValidatePromptCapabilities(PromptRequest prompt, AgentCapabilities? caps, out string errorMessage)
    {
        // Per docs: baseline support must include Text + ResourceLink.
        // If agent did not advertise richer prompt capabilities, reject those content types.
        var promptCaps = caps?.PromptCapabilities;
        var allowImage = promptCaps?.Image == true;
        var allowAudio = promptCaps?.Audio == true;
        var allowEmbedded = promptCaps?.EmbeddedContext == true;

        foreach (var block in prompt.Prompt)
        {
            switch (block)
            {
                case TextContent:
                case ResourceLink:
                case UnknownContentBlock:
                    continue;

                case ImageContent:
                    if (!allowImage)
                    {
                        errorMessage = "Prompt contains image content but agent did not advertise promptCapabilities.image.";
                        return false;
                    }
                    continue;

                case AudioContent:
                    if (!allowAudio)
                    {
                        errorMessage = "Prompt contains audio content but agent did not advertise promptCapabilities.audio.";
                        return false;
                    }
                    continue;

                case EmbeddedResource:
                    if (!allowEmbedded)
                    {
                        errorMessage = "Prompt contains embedded resource content but agent did not advertise promptCapabilities.embeddedContext.";
                        return false;
                    }
                    continue;

                default:
                    // forward-compatible: unknown derived types treated as allowed
                    continue;
            }
        }

        errorMessage = string.Empty;
        return true;
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
