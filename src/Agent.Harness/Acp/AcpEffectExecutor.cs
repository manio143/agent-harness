using System.Collections.Immutable;
using System.Text.Json;
using Agent.Acp.Acp;
using Agent.Harness.Llm;

using MeaiIChatClient = Microsoft.Extensions.AI.IChatClient;
using MeaiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using MeaiChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Agent.Harness.Acp;

public sealed class AcpEffectExecutor : IEffectExecutor
{
    private readonly string _sessionId;
    private readonly IAcpClientCaller _client;
    private readonly MeaiIChatClient _chat;
    private readonly IMcpToolInvoker _mcp;
    private readonly bool _logLlmPrompts;
    private readonly Agent.Harness.Persistence.ISessionStore? _store;

    public AcpEffectExecutor(
        string sessionId,
        IAcpClientCaller client,
        MeaiIChatClient chat,
        IMcpToolInvoker? mcp = null,
        bool logLlmPrompts = false,
        Agent.Harness.Persistence.ISessionStore? store = null)
    {
        _sessionId = sessionId;
        _client = client;
        _chat = chat;
        _mcp = mcp ?? NullMcpToolInvoker.Instance;
        _logLlmPrompts = logLlmPrompts;
        _store = store;
    }

    public async Task<ImmutableArray<ObservedChatEvent>> ExecuteAsync(SessionState state, Effect effect, CancellationToken cancellationToken)
    {
        switch (effect)
        {
            case CallModel:
                return await CallModelAsync(state, cancellationToken).ConfigureAwait(false);

            case CheckPermission p:
                return CheckPermission(state, p);

            case ExecuteToolCall t:
                return await ExecuteToolAsync(t, cancellationToken).ConfigureAwait(false);

            default:
                return ImmutableArray<ObservedChatEvent>.Empty;
        }
    }

    private ImmutableArray<ObservedChatEvent> CheckPermission(SessionState state, CheckPermission p)
    {
        // MVP: deterministic capability-only gating.
        var known = state.Tools.Any(t => t.Name == p.ToolName);
        if (!known)
            return ImmutableArray.Create<ObservedChatEvent>(new ObservedPermissionDenied(p.ToolId, "unknown_tool"));

        return ImmutableArray.Create<ObservedChatEvent>(new ObservedPermissionApproved(p.ToolId, "capability_present"));
    }

    private async Task<ImmutableArray<ObservedChatEvent>> CallModelAsync(SessionState state, CancellationToken cancellationToken)
    {
        var rendered = Core.RenderPrompt(state);

        var meaiMessages = rendered
            .Select(m => new MeaiChatMessage(m.Role switch
            {
                ChatRole.User => MeaiChatRole.User,
                ChatRole.Assistant => MeaiChatRole.Assistant,
                _ => MeaiChatRole.System,
            }, m.Text))
            .ToList();

        // Session metadata system prompt (client-/protocol-agnostic).
        var meta = _store?.TryLoadMetadata(_sessionId);
        var sessionPayload = JsonSerializer.Serialize(new
        {
            sessionId = _sessionId,
            cwd = meta?.Cwd,
            createdAtIso = meta?.CreatedAtIso,
            updatedAtIso = meta?.UpdatedAtIso,
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        meaiMessages.Insert(0, new MeaiChatMessage(MeaiChatRole.System, $"<session>{sessionPayload}</session>"));

        var options = new Microsoft.Extensions.AI.ChatOptions
        {
            ToolMode = Microsoft.Extensions.AI.ChatToolMode.Auto,
            AllowMultipleToolCalls = true,
            Tools = new List<Microsoft.Extensions.AI.AITool>(),
        };

        foreach (var t in state.Tools)
        {
            // Declare tools to the LLM (invocation handled by the harness).
            options.Tools.Add(Microsoft.Extensions.AI.AIFunctionFactory.CreateDeclaration(
                t.Name,
                t.Description ?? string.Empty,
                t.InputSchema,
                returnJsonSchema: null));
        }

        if (_logLlmPrompts)
        {
            var payload = JsonSerializer.Serialize(new
            {
                messages = meaiMessages.Select(m => new { role = m.Role.ToString(), content = m.Text }),
                tools = options.Tools
                    .OfType<Microsoft.Extensions.AI.AIFunctionDeclaration>()
                    .Select(d => new { d.Name, d.Description, jsonSchema = d.JsonSchema }),
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });

            Console.Error.WriteLine("[llm.prompt] " + payload);
        }

        var updates = _chat.GetStreamingResponseAsync(meaiMessages, options, cancellationToken);

        var observed = new List<ObservedChatEvent>();
        await foreach (var o in MeaiObservedEventSource.FromStreamingResponse(updates, cancellationToken).ConfigureAwait(false))
            observed.Add(o);

        return observed.ToImmutableArray();
    }

    private async Task<ImmutableArray<ObservedChatEvent>> ExecuteToolAsync(ExecuteToolCall t, CancellationToken cancellationToken)
    {
        // Args can be JsonElement (committed ToolCallRequested.Args) or a plain dictionary (from detection).
        var args = NormalizeArgs(t.Args);

        try
        {
            switch (t.ToolName)
            {
                case "read_text_file":
                {
                    var rawPath = GetRequiredString(args, "path");
                    var path = NormalizeFsPath(rawPath);
                    try
                    {
                        var resp = await _client.ReadTextFileAsync(new Agent.Acp.Schema.ReadTextFileRequest { SessionId = _sessionId, Path = path }, cancellationToken)
                            .ConfigureAwait(false);
                        return ImmutableArray.Create<ObservedChatEvent>(new ObservedToolCallCompleted(t.ToolId, JsonSerializer.SerializeToElement(new { content = resp.Content })));
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException($"fs/read_text_file failed for path={path}: {ex.Message}", ex);
                    }
                }

                case "write_text_file":
                {
                    var rawPath = GetRequiredString(args, "path");
                    var path = NormalizeFsPath(rawPath);
                    var content = GetRequiredString(args, "content");
                    try
                    {
                        await _client.WriteTextFileAsync(new Agent.Acp.Schema.WriteTextFileRequest { SessionId = _sessionId, Path = path, Content = content }, cancellationToken)
                            .ConfigureAwait(false);
                        return ImmutableArray.Create<ObservedChatEvent>(new ObservedToolCallCompleted(t.ToolId, JsonSerializer.SerializeToElement(new { ok = true })));
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException($"fs/write_text_file failed for path={path}: {ex.Message}", ex);
                    }
                }

                case "execute_command":
                {
                    if (!args.TryGetValue("command", out var cmdEl) || cmdEl.ValueKind != JsonValueKind.Array)
                        throw new InvalidOperationException("missing_required:command");

                    var command = cmdEl.Deserialize<string[]>() ?? Array.Empty<string>();
                    if (command.Length == 0)
                        throw new InvalidOperationException("missing_required:command");

                    var created = await _client.CreateTerminalAsync(new Agent.Acp.Schema.CreateTerminalRequest
                    {
                        SessionId = _sessionId,
                        Command = command[0],
                        Args = command.Skip(1).ToList(),
                    }, cancellationToken).ConfigureAwait(false);

                    // MVP: wait for exit then pull output.
                    await _client.WaitForTerminalExitAsync(new Agent.Acp.Schema.WaitForTerminalExitRequest { SessionId = _sessionId, TerminalId = created.TerminalId }, cancellationToken)
                        .ConfigureAwait(false);

                    var output = await _client.GetTerminalOutputAsync(new Agent.Acp.Schema.TerminalOutputRequest { SessionId = _sessionId, TerminalId = created.TerminalId }, cancellationToken)
                        .ConfigureAwait(false);

                    return ImmutableArray.Create<ObservedChatEvent>(new ObservedToolCallCompleted(t.ToolId, JsonSerializer.SerializeToElement(new
                    {
                        exitStatus = output.ExitStatus,
                        output = output.Output,
                        truncated = output.Truncated,
                    })));
                }

                default:
                {
                    if (_mcp.CanInvoke(t.ToolName))
                    {
                        var payload = await _mcp.InvokeAsync(t.ToolId, t.ToolName, t.Args, cancellationToken).ConfigureAwait(false);
                        return ImmutableArray.Create<ObservedChatEvent>(new ObservedToolCallCompleted(t.ToolId, payload));
                    }

                    return ImmutableArray.Create<ObservedChatEvent>(new ObservedToolCallFailed(t.ToolId, "unknown_tool"));
                }
            }
        }
        catch (Exception ex)
        {
            return ImmutableArray.Create<ObservedChatEvent>(new ObservedToolCallFailed(t.ToolId, ex.Message));
        }
    }

    private static Dictionary<string, JsonElement> NormalizeArgs(object args)
    {
        if (args is JsonElement je && je.ValueKind == JsonValueKind.Object)
        {
            return je.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        }

        // best-effort: serialize then parse
        var parsed = JsonSerializer.SerializeToElement(args);
        if (parsed.ValueKind != JsonValueKind.Object)
            return new Dictionary<string, JsonElement>();

        return parsed.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
    }

    private string NormalizeFsPath(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return rawPath;

        // NOTE: ACP does not mandate any filesystem sandbox policy. Different clients may accept/reject
        // different paths. We normalize to a precise absolute path so the client can reliably enforce
        // whatever policy it wants (e.g. acpx requiring absolute paths and cwd subtree).
        var cwd = _store?.TryLoadMetadata(_sessionId)?.Cwd;
        if (string.IsNullOrWhiteSpace(cwd))
            return Path.GetFullPath(rawPath);

        return Path.IsPathRooted(rawPath)
            ? Path.GetFullPath(rawPath)
            : Path.GetFullPath(Path.Combine(cwd, rawPath));
    }

    private static string GetRequiredString(Dictionary<string, JsonElement> obj, string name)
    {
        if (!obj.TryGetValue(name, out var v) || v.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException($"missing_required:{name}");

        return v.GetString() ?? "";
    }
}
