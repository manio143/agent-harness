using System.Collections.Immutable;
using System.Text.Json;
using Agent.Acp.Acp;
using Agent.Harness.Llm;
using Agent.Harness.Tools.Executors;

using MeaiIChatClient = Microsoft.Extensions.AI.IChatClient;
using MeaiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using MeaiChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Agent.Harness.Acp;

public sealed class HarnessEffectExecutor : IStreamingEffectExecutor
{
    private readonly string _sessionId;
    private readonly IAcpClientCaller _client;
    private readonly MeaiIChatClient _chat;
    private readonly Func<string, MeaiIChatClient>? _chatByModel;
    private readonly Func<string, bool>? _isKnownModel;
    private readonly IMcpToolInvoker _mcp;
    private readonly bool _logLlmPrompts;
    private readonly string? _sessionCwd;
    private readonly Agent.Harness.Persistence.ISessionStore? _store;
    private readonly string? _modelCatalogSystemPrompt;
    private readonly Agent.Harness.Threads.IThreadTools? _threadTools;
    private readonly Agent.Harness.Threads.IThreadObserver? _observer;
    private readonly Agent.Harness.Threads.IThreadLifecycle? _lifecycle;
    private readonly Agent.Harness.Threads.IThreadScheduler? _scheduler;
    private readonly string _threadId;

    private readonly ToolCallRouter _toolRouter;

    public HarnessEffectExecutor(
        string sessionId,
        IAcpClientCaller client,
        MeaiIChatClient chat,
        Func<string, MeaiIChatClient>? chatByModel = null,
        Func<string, bool>? isKnownModel = null,
        IMcpToolInvoker? mcp = null,
        bool logLlmPrompts = false,
        string? sessionCwd = null,
        Agent.Harness.Persistence.ISessionStore? store = null,
        string? modelCatalogSystemPrompt = null,
        Agent.Harness.Threads.IThreadTools? threadTools = null,
        Agent.Harness.Threads.IThreadObserver? observer = null,
        Agent.Harness.Threads.IThreadLifecycle? lifecycle = null,
        Agent.Harness.Threads.IThreadScheduler? scheduler = null,
        string threadId = Agent.Harness.Threads.ThreadIds.Main)
    {
        _sessionId = sessionId;
        _client = client;
        _chat = chat;
        _chatByModel = chatByModel;
        _isKnownModel = isKnownModel;
        _mcp = mcp ?? NullMcpToolInvoker.Instance;
        _logLlmPrompts = logLlmPrompts;
        _sessionCwd = sessionCwd;
        _store = store;
        _modelCatalogSystemPrompt = modelCatalogSystemPrompt;
        _threadTools = threadTools;
        _observer = observer;
        _lifecycle = lifecycle;
        _scheduler = scheduler;
        _threadId = threadId;

        _toolRouter = new ToolCallRouter(new IToolCallExecutor[]
        {
            new SystemToolCallExecutor(_threadTools, _observer, _lifecycle, _scheduler, _isKnownModel, _threadId),
            new McpToolCallExecutor(_mcp),
            new AcpHostToolCallExecutor(_sessionId, _client, sessionCwd: _sessionCwd, store: _store),
        });
    }

    public async Task<ImmutableArray<ObservedChatEvent>> ExecuteAsync(SessionState state, Effect effect, CancellationToken cancellationToken)
    {
        // Convenience wrapper: collect the streaming form.
        // Note: TurnRunner will *refuse* to execute CallModel via this non-streaming path.
        var list = new List<ObservedChatEvent>();
        await foreach (var o in ExecuteStreamingAsync(state, effect, cancellationToken).ConfigureAwait(false))
            list.Add(o);
        return list.ToImmutableArray();
    }

    public async IAsyncEnumerable<ObservedChatEvent> ExecuteStreamingAsync(SessionState state, Effect effect, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        switch (effect)
        {
            case CallModel call:
                await foreach (var o in CallModelStreamingAsync(state, call, cancellationToken).ConfigureAwait(false))
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
            {
                var observations = await _toolRouter.ExecuteAsync(state, t, cancellationToken).ConfigureAwait(false);
                foreach (var o in observations)
                    yield return o;
                yield break;
            }

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

    private async IAsyncEnumerable<ObservedChatEvent> CallModelStreamingAsync(SessionState state, CallModel call, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
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

            if (!string.IsNullOrWhiteSpace(_modelCatalogSystemPrompt))
                meaiMessages.Insert(0, new MeaiChatMessage(MeaiChatRole.System, _modelCatalogSystemPrompt));


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

            var chat = _chatByModel is null ? _chat : _chatByModel(call.Model);
            var updates = chat.GetStreamingResponseAsync(meaiMessages, options, cancellationToken);

            await foreach (var o in MeaiObservedEventSource.FromStreamingResponse(updates, cancellationToken).ConfigureAwait(false))
                yield return o;
        }
        finally
        {
        }
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
}
