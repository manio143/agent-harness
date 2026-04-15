using System.Collections.Immutable;
using System.Text.Json;
using Agent.Acp.Acp;
using Agent.Harness.Llm;

using MeaiIChatClient = Microsoft.Extensions.AI.IChatClient;
using MeaiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using MeaiChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Agent.Harness.Acp;

public sealed class AcpEffectExecutor : IStreamingEffectExecutor
{
    private readonly string _sessionId;
    private readonly IAcpClientCaller _client;
    private readonly MeaiIChatClient _chat;
    private readonly IMcpToolInvoker _mcp;
    private readonly bool _logLlmPrompts;
    private readonly string? _sessionCwd;
    private readonly Agent.Harness.Persistence.ISessionStore? _store;
    private readonly Agent.Harness.Threads.ThreadManager? _threads;
    private readonly Agent.Harness.Threads.IThreadScheduler? _scheduler;
    private readonly string _threadId;

    public AcpEffectExecutor(
        string sessionId,
        IAcpClientCaller client,
        MeaiIChatClient chat,
        IMcpToolInvoker? mcp = null,
        bool logLlmPrompts = false,
        string? sessionCwd = null,
        Agent.Harness.Persistence.ISessionStore? store = null,
        Agent.Harness.Threads.ThreadManager? threads = null,
        Agent.Harness.Threads.IThreadScheduler? scheduler = null,
        string threadId = Agent.Harness.Threads.ThreadIds.Main)
    {
        _sessionId = sessionId;
        _client = client;
        _chat = chat;
        _mcp = mcp ?? NullMcpToolInvoker.Instance;
        _logLlmPrompts = logLlmPrompts;
        _sessionCwd = sessionCwd;
        _store = store;
        _threads = threads;
        _scheduler = scheduler;
        _threadId = threadId;
    }

    public async Task<ImmutableArray<ObservedChatEvent>> ExecuteAsync(SessionState state, Effect effect, CancellationToken cancellationToken)
    {
        // Back-compat buffering wrapper.
        var list = new List<ObservedChatEvent>();
        await foreach (var o in ExecuteStreamingAsync(state, effect, cancellationToken).ConfigureAwait(false))
            list.Add(o);
        return list.ToImmutableArray();
    }

    public async IAsyncEnumerable<ObservedChatEvent> ExecuteStreamingAsync(SessionState state, Effect effect, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        switch (effect)
        {
            case CallModel:
                await foreach (var o in CallModelStreamingAsync(state, cancellationToken).ConfigureAwait(false))
                    yield return o;
                yield break;

            case CheckPermission p:
                foreach (var o in CheckPermission(state, p))
                    yield return o;
                yield break;

            case ExecuteToolCall t:
                foreach (var o in await ExecuteToolAsync(state, t, cancellationToken).ConfigureAwait(false))
                    yield return o;
                yield break;

            default:
                yield break;
        }
    }

    private ImmutableArray<ObservedChatEvent> CheckPermission(SessionState state, CheckPermission p)
    {
        // MVP: deterministic capability-only gating.
        // Harness-internal tools are always allowed.
        if (p.ToolName is "report_intent" or "thread_list" or "thread_new" or "thread_fork" or "thread_send" or "thread_read")
            return ImmutableArray.Create<ObservedChatEvent>(new ObservedPermissionApproved(p.ToolId, "internal_tool"));

        var known = state.Tools.Any(t => t.Name == p.ToolName);
        if (!known)
            return ImmutableArray.Create<ObservedChatEvent>(new ObservedPermissionDenied(p.ToolId, "unknown_tool"));

        return ImmutableArray.Create<ObservedChatEvent>(new ObservedPermissionApproved(p.ToolId, "capability_present"));
    }

    private async IAsyncEnumerable<ObservedChatEvent> CallModelStreamingAsync(SessionState state, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            // Drain inbox BEFORE marking running. Enqueue-delivery messages are eligible when the
            // thread is idle (i.e. just before starting the next model call).
            var inbox = _threads?.DrainInboxForPrompt(_threadId) ?? ImmutableArray<Agent.Harness.Threads.ThreadEnvelope>.Empty;

            _threads?.MarkRunning(_threadId);

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

            // Inbox injection (main thread): convert thread->thread messages into system messages.
            foreach (var env in inbox.Reverse())
            {
                var payload = JsonSerializer.Serialize(env, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                meaiMessages.Insert(1, new MeaiChatMessage(MeaiChatRole.System, $"<inbox>{payload}</inbox>"));
            }

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
            var promptPayload = new
            {
                messages = meaiMessages.Select(m => new { role = m.Role.ToString(), content = m.Text }),
                tools = options.Tools
                    .OfType<Microsoft.Extensions.AI.AIFunctionDeclaration>()
                    .Select(d => new { d.Name, d.Description, jsonSchema = d.JsonSchema }),
            };

            // Console for interactive runs
            var consolePayload = JsonSerializer.Serialize(promptPayload, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
            Console.Error.WriteLine("[llm.prompt] " + consolePayload);

            // File for acpx runs (stderr may be swallowed)
            TryAppendPromptLog(promptPayload);
        }

            var updates = _chat.GetStreamingResponseAsync(meaiMessages, options, cancellationToken);

            await foreach (var o in MeaiObservedEventSource.FromStreamingResponse(updates, cancellationToken).ConfigureAwait(false))
                yield return o;
        }
        finally
        {
            _threads?.MarkIdle(_threadId);
        }
    }

    private async Task<ImmutableArray<ObservedChatEvent>> ExecuteToolAsync(SessionState state, ExecuteToolCall t, CancellationToken cancellationToken)
    {
        // Args can be JsonElement (committed ToolCallRequested.Args) or a plain dictionary (from detection).
        var args = NormalizeArgs(t.Args);

        try
        {
            switch (t.ToolName)
            {
                case "report_intent":
                {
                    if (_threads is not null)
                    {
                        var intent = GetRequiredString(args, "intent");
                        _threads.ReportIntent(_threadId, intent);
                    }

                    return ImmutableArray.Create<ObservedChatEvent>(
                        new ObservedToolCallCompleted(t.ToolId, JsonSerializer.SerializeToElement(new { ok = true })));
                }

                case "thread_list":
                {
                    var threads = _threads?.List() ?? ImmutableArray<Agent.Harness.Threads.ThreadInfo>.Empty;
                    return ImmutableArray.Create<ObservedChatEvent>(new ObservedToolCallCompleted(
                        t.ToolId,
                        JsonSerializer.SerializeToElement(new { threads })));
                }

                case "thread_read":
                {
                    var threadId = GetRequiredString(args, "threadId");
                    var messages = _threads?.ReadAssistantMessages(threadId) ?? ImmutableArray<Agent.Harness.Threads.ThreadMessage>.Empty;
                    return ImmutableArray.Create<ObservedChatEvent>(new ObservedToolCallCompleted(
                        t.ToolId,
                        JsonSerializer.SerializeToElement(new { messages })));
                }

                case "thread_new":
                {
                    var message = GetRequiredString(args, "message");
                    var delivery = ParseDelivery(args);
                    var id = _threads?.New(_threadId, message, delivery) ?? "";
                    if (!string.IsNullOrWhiteSpace(id) && delivery == Agent.Harness.Threads.InboxDelivery.Immediate)
                        _scheduler?.ScheduleRun(id);

                    return ImmutableArray.Create<ObservedChatEvent>(new ObservedToolCallCompleted(
                        t.ToolId,
                        JsonSerializer.SerializeToElement(new { threadId = id })));
                }

                case "thread_fork":
                {
                    var message = GetRequiredString(args, "message");
                    var delivery = ParseDelivery(args);
                    var id = _threads?.Fork(_threadId, state, message, delivery) ?? "";
                    if (!string.IsNullOrWhiteSpace(id) && delivery == Agent.Harness.Threads.InboxDelivery.Immediate)
                        _scheduler?.ScheduleRun(id);

                    return ImmutableArray.Create<ObservedChatEvent>(new ObservedToolCallCompleted(
                        t.ToolId,
                        JsonSerializer.SerializeToElement(new { threadId = id })));
                }

                case "thread_send":
                {
                    var threadId = GetRequiredString(args, "threadId");
                    var message = GetRequiredString(args, "message");
                    var delivery = ParseDelivery(args);

                    // Universal intake: express as an observed inbox arrival for the target thread.
                    if (_scheduler is Agent.Harness.Threads.ThreadOrchestrator orchestrator)
                    {
                        var now = DateTimeOffset.UtcNow.ToString("O");
                        var envId = Agent.Harness.Threads.ThreadEnvelopes.NewEnvelopeId();

                        orchestrator.Observe(threadId, new ObservedInboxMessageArrived(
                            ThreadId: threadId,
                            Kind: Agent.Harness.Threads.ThreadInboxMessageKind.InterThreadMessage,
                            Delivery: delivery,
                            EnvelopeId: envId,
                            EnqueuedAtIso: now,
                            Source: "thread",
                            SourceThreadId: _threadId,
                            Text: message,
                            Meta: null));

                        if (delivery == Agent.Harness.Threads.InboxDelivery.Immediate)
                            _scheduler?.ScheduleRun(threadId);
                    }
                    else
                    {
                        // Fallback (legacy): direct enqueue.
                        _threads?.Send(_threadId, threadId, message, delivery);
                        if (delivery == Agent.Harness.Threads.InboxDelivery.Immediate)
                            _scheduler?.ScheduleRun(threadId);
                    }

                    return ImmutableArray.Create<ObservedChatEvent>(new ObservedToolCallCompleted(
                        t.ToolId,
                        JsonSerializer.SerializeToElement(new { ok = true })));
                }

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

    private static Agent.Harness.Threads.InboxDelivery ParseDelivery(Dictionary<string, JsonElement> args)
    {
        if (!args.TryGetValue("delivery", out var el) || el.ValueKind != JsonValueKind.String)
            return Agent.Harness.Threads.InboxDelivery.Immediate;

        var v = el.GetString();
        return v switch
        {
            "enqueue" => Agent.Harness.Threads.InboxDelivery.Enqueue,
            "immediate" => Agent.Harness.Threads.InboxDelivery.Immediate,
            _ => Agent.Harness.Threads.InboxDelivery.Immediate,
        };
    }

    private static Dictionary<string, JsonElement> NormalizeArgs(object args)
    { 
        if (args is JsonElement je && je.ValueKind == JsonValueKind.Object)
        {
            return je.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);
        }

        // best-effort: serialize then parse
        var parsed = JsonSerializer.SerializeToElement(args);
        if (parsed.ValueKind != JsonValueKind.Object)
            return new Dictionary<string, JsonElement>();

        return parsed.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);
    }

    private void TryAppendPromptLog(object promptPayload)
    {
        try
        {
            if (_store is not Agent.Harness.Persistence.JsonlSessionStore js)
                return;

            var sessionDir = Path.Combine(js.RootDir, _sessionId);
            Directory.CreateDirectory(sessionDir);

            var path = Path.Combine(sessionDir, "llm.prompt.jsonl");
            var line = JsonSerializer.Serialize(promptPayload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            File.AppendAllText(path, line + "\n");
        }
        catch
        {
            // best-effort logging only
        }
    }

    private string NormalizeFsPath(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return rawPath;

        // NOTE: ACP does not mandate any filesystem sandbox policy. Different clients may accept/reject
        // different paths. We normalize to a precise absolute path so the client can reliably enforce
        // whatever policy it wants (e.g. acpx requiring absolute paths and cwd subtree).
        var cwd = _sessionCwd;
        if (string.IsNullOrWhiteSpace(cwd))
            cwd = _store?.TryLoadMetadata(_sessionId)?.Cwd;

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
