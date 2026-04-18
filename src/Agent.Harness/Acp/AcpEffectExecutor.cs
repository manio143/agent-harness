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
    private readonly Agent.Harness.Threads.IThreadTools? _threadTools;
    private readonly Agent.Harness.Threads.IThreadObserver? _observer;
    private readonly Agent.Harness.Threads.IThreadLifecycle? _lifecycle;
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
        Agent.Harness.Threads.IThreadTools? threadTools = null,
        Agent.Harness.Threads.IThreadObserver? observer = null,
        Agent.Harness.Threads.IThreadLifecycle? lifecycle = null,
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
        _threadTools = threadTools;
        _observer = observer;
        _lifecycle = lifecycle;
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

            case ScheduleWake w:
                yield return new ObservedWakeModel(w.ThreadId);
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
        // Tool catalog is the source of truth. If it's present, it is runnable.
        // Capability checks must happen before tools enter the catalog.
        var known = state.Tools.Any(t => t.Name == p.ToolName);
        if (!known)
            return ImmutableArray.Create<ObservedChatEvent>(new ObservedPermissionDenied(p.ToolId, "unknown_tool"));

        return ImmutableArray.Create<ObservedChatEvent>(new ObservedPermissionApproved(p.ToolId, "tool_in_catalog"));
    }

    private async IAsyncEnumerable<ObservedChatEvent> CallModelStreamingAsync(SessionState state, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {

            var meaiMessages = Agent.Harness.Llm.MeaiPromptRenderer.Render(state);

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
            static object SerializeMessage(Microsoft.Extensions.AI.ChatMessage m)
            {
                // Prefer lossless-ish logging: include text plus a best-effort summary of structured contents.
                var contents = m.Contents is null
                    ? Array.Empty<object>()
                    : m.Contents
                        .Select(c => (object)(c switch
                        {
                            Microsoft.Extensions.AI.TextContent tc => new Dictionary<string, object?>
                            {
                                ["type"] = "text",
                                ["text"] = tc.Text,
                            },
                            Microsoft.Extensions.AI.FunctionCallContent fc => new Dictionary<string, object?>
                            {
                                ["type"] = "function_call",
                                ["callId"] = fc.CallId,
                                ["name"] = fc.Name,
                                ["arguments"] = fc.Arguments,
                            },
                            Microsoft.Extensions.AI.FunctionResultContent fr => new Dictionary<string, object?>
                            {
                                ["type"] = "function_result",
                                ["callId"] = fr.CallId,
                                ["result"] = fr.Result,
                            },
                            _ => new Dictionary<string, object?>
                            {
                                ["type"] = c.GetType().Name,
                            },
                        }))
                        .ToArray();

                return new
                {
                    role = m.Role.ToString(),
                    text = m.Text,
                    contents,
                };
            }

            var promptPayload = new
            {
                messages = meaiMessages.Select(m => SerializeMessage(m)),
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
                    var intent = GetRequiredString(args, "intent");
                    
                    // Update thread metadata
                    _threadTools?.ReportIntent(_threadId, intent);

                    return ImmutableArray.Create<ObservedChatEvent>(
                        new ObservedToolCallCompleted(t.ToolId, JsonSerializer.SerializeToElement(new { ok = true })));
                }

                case "thread_list":
                {
                    var threads = _threadTools?.List() ?? ImmutableArray<Agent.Harness.Threads.ThreadInfo>.Empty;
                    return ImmutableArray.Create<ObservedChatEvent>(new ObservedToolCallCompleted(
                        t.ToolId,
                        JsonSerializer.SerializeToElement(new { threads })));
                }

                case "thread_read":
                {
                    var threadId = GetRequiredString(args, "threadId");
                    var messages = _threadTools?.ReadThreadMessages(threadId) ?? ImmutableArray<Agent.Harness.Threads.ThreadMessage>.Empty;
                    return ImmutableArray.Create<ObservedChatEvent>(new ObservedToolCallCompleted(
                        t.ToolId,
                        JsonSerializer.SerializeToElement(new { messages })));
                }

                case "thread_new":
                {
                    var message = GetRequiredString(args, "message");
                    var delivery = ParseDelivery(args);

                    if (_lifecycle is null || _observer is null || _scheduler is null)
                        throw new InvalidOperationException("thread_tools_require_orchestrator");

                    // Unified model: thread lifecycle is owned by the orchestrator.
                    // We must return a threadId synchronously, so we preallocate it.
                    var id = "thr_" + Guid.NewGuid().ToString("N")[..12];

                    await _lifecycle.RequestForkChildThreadAsync(
                        _threadId,
                        id,
                        state.Committed,
                        cancellationToken).ConfigureAwait(false);

                    // Universal intake: express initial message as observed inbox arrival to the child thread.
                    await _observer.ObserveAsync(
                        id,
                        Agent.Harness.Threads.ThreadInboxArrivals.InterThreadMessage(
                            threadId: id,
                            text: message,
                            sourceThreadId: _threadId,
                            source: "thread",
                            delivery: delivery),
                        cancellationToken).ConfigureAwait(false);

                    if (delivery == Agent.Harness.Threads.InboxDelivery.Immediate)
                    {
                        _scheduler.ScheduleRun(id);
                    }

                    return ImmutableArray.Create<ObservedChatEvent>(new ObservedToolCallCompleted(
                        t.ToolId,
                        JsonSerializer.SerializeToElement(new { threadId = id })));
                }

                case "thread_fork":
                {
                    var message = GetRequiredString(args, "message");
                    var delivery = ParseDelivery(args);

                    if (_lifecycle is null || _observer is null || _scheduler is null)
                        throw new InvalidOperationException("thread_tools_require_orchestrator");

                    // Unified model: thread lifecycle is owned by the orchestrator.
                    var id = "thr_" + Guid.NewGuid().ToString("N")[..12];

                    await _lifecycle.RequestForkChildThreadAsync(
                        _threadId,
                        id,
                        state.Committed,
                        cancellationToken).ConfigureAwait(false);

                    await _observer.ObserveAsync(
                        id,
                        Agent.Harness.Threads.ThreadInboxArrivals.InterThreadMessage(
                            threadId: id,
                            text: message,
                            sourceThreadId: _threadId,
                            source: "thread",
                            delivery: delivery),
                        cancellationToken).ConfigureAwait(false);

                    if (delivery == Agent.Harness.Threads.InboxDelivery.Immediate)
                    {
                        _scheduler.ScheduleRun(id);
                    }

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
                    // IMPORTANT: if the target is the current thread, do NOT go through ThreadOrchestrator.ObserveAsync
                    // (it is thread-gated and would deadlock inside an in-flight turn).
                    var inboxArrived = Agent.Harness.Threads.ThreadInboxArrivals.InterThreadMessage(
                        threadId: threadId,
                        text: message,
                        sourceThreadId: _threadId,
                        source: "thread",
                        delivery: delivery);

                    if (threadId == _threadId)
                    {
                        return ImmutableArray.Create<ObservedChatEvent>(
                            inboxArrived,
                            new ObservedToolCallCompleted(t.ToolId, JsonSerializer.SerializeToElement(new { ok = true })));
                    }

                    if (_observer is null || _scheduler is null)
                        throw new InvalidOperationException("thread_tools_require_orchestrator");

                    await _observer.ObserveAsync(threadId, inboxArrived, cancellationToken).ConfigureAwait(false);

                    if (delivery == Agent.Harness.Threads.InboxDelivery.Immediate)
                    {
                        _scheduler.ScheduleRun(threadId);
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

                        var sha256 = Sha256Hex(resp.Content);

                        return ImmutableArray.Create<ObservedChatEvent>(new ObservedToolCallCompleted(
                            t.ToolId,
                            JsonSerializer.SerializeToElement(new { content = resp.Content, sha256 })));
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

                case "patch_text_file":
                {
                    var rawPath = GetRequiredString(args, "path");
                    var path = NormalizeFsPath(rawPath);

                    var expectedSha = args.TryGetValue("expectedSha256", out var expEl) && expEl.ValueKind == JsonValueKind.String
                        ? expEl.GetString()
                        : null;

                    if (!args.TryGetValue("edits", out var editsEl) || editsEl.ValueKind != JsonValueKind.Array)
                        throw new InvalidOperationException("missing_required:edits");

                    var before = await _client.ReadTextFileAsync(new Agent.Acp.Schema.ReadTextFileRequest { SessionId = _sessionId, Path = path }, cancellationToken)
                        .ConfigureAwait(false);

                    var content = before.Content;
                    var beforeSha = Sha256Hex(content);

                    if (!string.IsNullOrWhiteSpace(expectedSha))
                    {
                        if (!IsSha256Hex(expectedSha))
                            throw new InvalidOperationException($"invalid_args:expectedSha256_not_sha256:{expectedSha}");

                        if (!string.Equals(expectedSha, beforeSha, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException($"sha256_mismatch expected={expectedSha} actual={beforeSha}");
                    }

                    foreach (var edit in editsEl.EnumerateArray())
                    {
                        content = ApplyStructuredEdit(content, edit);
                    }

                    await _client.WriteTextFileAsync(new Agent.Acp.Schema.WriteTextFileRequest { SessionId = _sessionId, Path = path, Content = content }, cancellationToken)
                        .ConfigureAwait(false);

                    var afterSha = Sha256Hex(content);

                    return ImmutableArray.Create<ObservedChatEvent>(new ObservedToolCallCompleted(t.ToolId, JsonSerializer.SerializeToElement(new
                    {
                        ok = true,
                        beforeSha256 = beforeSha,
                        afterSha256 = afterSha,
                        appliedEdits = editsEl.GetArrayLength(),
                    })));
                }

                case "execute_command":
                {
                    var command = GetRequiredString(args, "command");

                    var argv = Array.Empty<string>();
                    if (args.TryGetValue("args", out var argsEl) && argsEl.ValueKind == JsonValueKind.Array)
                    {
                        argv = argsEl.Deserialize<string[]>() ?? Array.Empty<string>();
                    }

                    var created = await _client.CreateTerminalAsync(new Agent.Acp.Schema.CreateTerminalRequest
                    {
                        SessionId = _sessionId,
                        Command = command,
                        Args = argv.ToList(),
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

    private static bool IsSha256Hex(string s)
    {
        if (s.Length != 64)
            return false;

        foreach (var ch in s)
        {
            var isHex =
                (ch >= '0' && ch <= '9') ||
                (ch >= 'a' && ch <= 'f') ||
                (ch >= 'A' && ch <= 'F');

            if (!isHex)
                return false;
        }

        return true;
    }

    private static string Sha256Hex(string content)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content ?? string.Empty));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string ApplyStructuredEdit(string content, JsonElement edit)
    {
        if (edit.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("invalid_args:edit_not_object");

        if (!edit.TryGetProperty("op", out var opEl) || opEl.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("invalid_args:missing_required:op");

        var op = opEl.GetString();

        int? occurrence = null;
        if (edit.TryGetProperty("occurrence", out var occEl) && occEl.ValueKind == JsonValueKind.Number)
            occurrence = occEl.GetInt32();

        return op switch
        {
            "replace_exact" => ApplyReplaceExact(content,
                GetRequiredString(edit, "oldText"),
                GetRequiredString(edit, "newText"),
                occurrence),

            "delete_exact" => ApplyDeleteExact(content,
                GetRequiredString(edit, "text"),
                occurrence),

            "insert_before" => ApplyInsert(content,
                GetRequiredString(edit, "anchorText"),
                GetRequiredString(edit, "text"),
                after: false,
                occurrence),

            "insert_after" => ApplyInsert(content,
                GetRequiredString(edit, "anchorText"),
                GetRequiredString(edit, "text"),
                after: true,
                occurrence),

            _ => throw new InvalidOperationException($"invalid_args:unknown_op:{op}"),
        };
    }

    private static string GetRequiredString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException($"invalid_args:missing_required:{name}");
        return el.GetString() ?? string.Empty;
    }

    private static string ApplyDeleteExact(string content, string text, int? occurrence)
    {
        if (string.IsNullOrEmpty(text))
            throw new InvalidOperationException("invalid_args:text_empty");

        return ApplyReplaceExact(content, text, string.Empty, occurrence);
    }

    private static string ApplyReplaceExact(string content, string oldText, string newText, int? occurrence)
    {
        if (string.IsNullOrEmpty(oldText))
            throw new InvalidOperationException("invalid_args:oldText_empty");

        var idxs = AllIndexesOf(content, oldText);
        var idx = SelectIndex(idxs, oldText, occurrence);

        return content.Substring(0, idx) + newText + content.Substring(idx + oldText.Length);
    }

    private static string ApplyInsert(string content, string anchor, string text, bool after, int? occurrence)
    {
        if (string.IsNullOrEmpty(anchor))
            throw new InvalidOperationException("invalid_args:anchorText_empty");

        if (string.IsNullOrEmpty(text))
            throw new InvalidOperationException("invalid_args:text_empty");

        var idxs = AllIndexesOf(content, anchor);
        var idx = SelectIndex(idxs, anchor, occurrence);
        var insertAt = after ? idx + anchor.Length : idx;

        return content.Substring(0, insertAt) + text + content.Substring(insertAt);
    }

    private static List<int> AllIndexesOf(string content, string needle)
    {
        var idxs = new List<int>();
        for (var i = 0;;)
        {
            var idx = content.IndexOf(needle, i, StringComparison.Ordinal);
            if (idx < 0) break;
            idxs.Add(idx);
            i = idx + Math.Max(needle.Length, 1);
        }

        return idxs;
    }

    private static int SelectIndex(List<int> idxs, string needle, int? occurrence)
    {
        if (occurrence is null)
        {
            if (idxs.Count == 0)
                throw new InvalidOperationException($"patch_failed:not_found:{needle}");
            if (idxs.Count != 1)
                throw new InvalidOperationException($"patch_failed:multiple_matches:{needle}:{idxs.Count}");
            return idxs[0];
        }

        if (idxs.Count == 0)
            throw new InvalidOperationException($"patch_failed:not_found:{needle}");
        if (occurrence.Value < 0 || occurrence.Value >= idxs.Count)
            throw new InvalidOperationException($"patch_failed:occurrence_out_of_range:{needle}:{occurrence.Value}:{idxs.Count}");

        return idxs[occurrence.Value];
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
