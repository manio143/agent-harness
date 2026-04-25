using System.Collections.Immutable;
using System.Text.Json;
using Agent.Acp.Acp;
using Agent.Harness.Llm;
using Agent.Harness.Llm.SystemPrompts;
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
    private readonly Func<string, string?>? _providerModelByFriendlyName;
    private readonly Func<string, int?>? _maxOutputTokensByFriendlyName;
    private readonly Func<string, bool>? _isKnownModel;
    private readonly IMcpToolInvoker _mcp;
    private readonly bool _logLlmPrompts;
    private readonly string? _sessionCwd;
    private readonly Agent.Harness.Persistence.ISessionStore? _store;
    private readonly string? _modelCatalogSystemPrompt;
    private readonly SystemPromptComposer _systemPromptComposer;
    private readonly int _compactionTailMessageCount;
    private readonly int? _compactionMaxTailMessageChars;
    private readonly string _compactionModel;
    private readonly Agent.Harness.Threads.IThreadStore? _threadStore;
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
        Func<string, string?>? providerModelByFriendlyName = null,
        Func<string, int?>? maxOutputTokensByFriendlyName = null,
        Func<string, bool>? isKnownModel = null,
        IMcpToolInvoker? mcp = null,
        bool logLlmPrompts = false,
        string? sessionCwd = null,
        Agent.Harness.Persistence.ISessionStore? store = null,
        string? modelCatalogSystemPrompt = null,
        int compactionTailMessageCount = 5,
        int? compactionMaxTailMessageChars = null,
        string compactionModel = "default",
        SystemPromptComposer? systemPromptComposer = null,
        Agent.Harness.Threads.IThreadStore? threadStore = null,
        Agent.Harness.Threads.IThreadTools? threadTools = null,
        Agent.Harness.Threads.IThreadObserver? observer = null,
        Agent.Harness.Threads.IThreadLifecycle? lifecycle = null,
        Agent.Harness.Threads.IThreadScheduler? scheduler = null,
        Agent.Harness.Threads.IThreadIdAllocator? threadIdAllocator = null,
        string threadId = Agent.Harness.Threads.ThreadIds.Main)
    {
        _sessionId = sessionId;
        _client = client;
        _chat = chat;
        _chatByModel = chatByModel;
        _providerModelByFriendlyName = providerModelByFriendlyName;
        _maxOutputTokensByFriendlyName = maxOutputTokensByFriendlyName;
        _isKnownModel = isKnownModel;
        _mcp = mcp ?? NullMcpToolInvoker.Instance;
        _logLlmPrompts = logLlmPrompts;
        _sessionCwd = sessionCwd;
        _store = store;
        _modelCatalogSystemPrompt = modelCatalogSystemPrompt;
        _compactionTailMessageCount = compactionTailMessageCount;
        _compactionMaxTailMessageChars = compactionMaxTailMessageChars;
        _compactionModel = compactionModel;
        _threadStore = threadStore;
        _systemPromptComposer = systemPromptComposer ?? new SystemPromptComposer(new ISystemPromptContributor[]
        {
            new ModelCatalogSystemPromptContributor(),
            new ToolCallingPolicySystemPromptContributor(),
            new SessionEnvelopeSystemPromptContributor(),
            new ThreadEnvelopeSystemPromptContributor(),
            new ThreadCapabilitiesSystemPromptContributor(),
            new ThreadingGuidanceSystemPromptContributor(),
        });
        _threadTools = threadTools;
        _observer = observer;
        _lifecycle = lifecycle;
        _scheduler = scheduler;
        _threadId = threadId;

        var allocator = threadIdAllocator
            ?? (_threadTools is not null
                ? new Agent.Harness.Threads.RandomSuffixThreadIdAllocator(_threadTools, new Agent.Harness.Threads.GuidHexSuffixGenerator(), suffixChars: 4)
                : null);

        Func<SessionState, ExecuteToolCall, bool>? gate = null;
        if (_threadStore is not null)
        {
            gate = (state, tool) => Agent.Harness.Threads.ThreadCapabilitiesEvaluator.IsToolAllowed(
                _sessionId,
                _threadId,
                tool.ToolName,
                state.Tools,
                _threadStore);
        }

        var systemRegistry = new Agent.Harness.Tools.Handlers.ToolRegistry(new Agent.Harness.Tools.Handlers.IToolHandler[]
        {
            new Agent.Harness.Tools.Handlers.ReportIntentToolHandler(_threadTools, _threadId),
            new Agent.Harness.Tools.Handlers.ThreadListToolHandler(_threadTools),
            new Agent.Harness.Tools.Handlers.ThreadReadToolHandler(_threadTools),
            new Agent.Harness.Tools.Handlers.ThreadSendToolHandler(_threadTools, _observer, _scheduler, _threadId),
            new Agent.Harness.Tools.Handlers.ThreadStartToolHandler(_threadTools, _lifecycle, _observer, _scheduler, allocator, _isKnownModel, _threadId),
            new Agent.Harness.Tools.Handlers.ThreadConfigToolHandler(_threadTools, _lifecycle, _threadId, _isKnownModel),
            new Agent.Harness.Tools.Handlers.ThreadStopToolHandler(_lifecycle),
        });

        _toolRouter = new ToolCallRouter(new IToolCallExecutor[]
        {
            new Agent.Harness.Tools.Executors.RegistryToolCallExecutor(systemRegistry),
            new McpToolCallExecutor(_mcp),
            new AcpHostToolCallExecutor(_sessionId, _client, sessionCwd: _sessionCwd, store: _store),
        }, gate);
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
                observations = CapToolResultsIfNeeded(state, t, observations);

                foreach (var o in observations)
                    yield return o;
                yield break;
            }

            case RunCompaction c:
            {
                var model = string.IsNullOrWhiteSpace(_compactionModel) ? ResolveModelFriendly(state) : _compactionModel;
                var chat = _chatByModel is null ? _chat : _chatByModel(model);
                var providerModel = _providerModelByFriendlyName?.Invoke(model);

                var transcript = Agent.Harness.Compaction.CompactionTranscriptBuilder.Build(state.Committed);

                yield return new ObservedThreadCompactionStarted(c.ThreadId, model, providerModel, CompactionSystemPrompt);

                var system = new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.System, CompactionSystemPrompt);
                var user = new Microsoft.Extensions.AI.ChatMessage(
                    Microsoft.Extensions.AI.ChatRole.User,
                    "<transcript>\n" + transcript + "\n</transcript>\n\n" +
                    "<task>Return exactly one <compaction>...</compaction> block.</task>");

                var resp = await chat.GetResponseAsync(new[] { system, user }, cancellationToken: cancellationToken).ConfigureAwait(false);

                var text = Agent.Harness.Compaction.CompactionResponseParser.Parse(resp.Text);

                yield return new ObservedThreadCompactedGenerated(c.ThreadId, text);
                yield break;
            }

            default:
                yield break;
        }
    }

    private ImmutableArray<ObservedChatEvent> CapToolResultsIfNeeded(SessionState state, ExecuteToolCall call, ImmutableArray<ObservedChatEvent> observations)
    {
        if (!Agent.Harness.Llm.ToolResultSanitizer.IsEnabled)
            return observations;

        // Only cap completed results (progress updates are already small and frequent).
        var changed = false;
        var list = new List<ObservedChatEvent>(observations.Length);

        foreach (var o in observations)
        {
            if (o is not ObservedToolCallCompleted completed)
            {
                list.Add(o);
                continue;
            }

            var rawJson = completed.Result switch
            {
                JsonElement el => el.GetRawText(),
                string s => s,
                _ => JsonSerializer.Serialize(completed.Result, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            };

            var sanitized = Agent.Harness.Llm.ToolResultSanitizer.Sanitize(completed.Result);
            if (!sanitized.WasTruncated)
            {
                list.Add(o);
                continue;
            }

            changed = true;

            // If tool is not file read, persist the raw tool result to a file in the thread folder
            // so the model can pull it via read_text_file or execute_command.
            string? rawFile = null;
            if (call.ToolName != "read_text_file")
                rawFile = TryWriteRawToolResult(rawJson);

            object wrapped = WrapCappedToolResult(call.ToolName, completed.Result, sanitized.Value, rawFile);

            list.Add(new ObservedToolCallCompleted(completed.ToolId, wrapped));
        }

        return changed ? list.ToImmutableArray() : observations;
    }

    private object WrapCappedToolResult(string toolName, object originalResult, object? sanitizedValue, string? rawFile)
    {
        // Prefer to preserve object shape when possible.
        Dictionary<string, object?> dict;

        if (sanitizedValue is Dictionary<string, object?> d)
        {
            dict = new Dictionary<string, object?>(d, StringComparer.Ordinal);
        }
        else
        {
            dict = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["value"] = sanitizedValue,
            };
        }

        dict["_truncated"] = true;

        if (!string.IsNullOrWhiteSpace(rawFile))
        {
            dict["_raw_result_file"] = rawFile;
        }

        // Special-case: read_text_file should include line info when truncated.
        if (toolName == "read_text_file")
        {
            try
            {
                // originalResult is usually { content, sha256 } from AcpHostToolCallExecutor.
                var el = originalResult is JsonElement je
                    ? je
                    : JsonSerializer.SerializeToElement(originalResult, new JsonSerializerOptions(JsonSerializerDefaults.Web));

                if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                {
                    var content = c.GetString() ?? string.Empty;
                    var lines = content.Length == 0 ? 0 : 1 + content.Count(ch => ch == '\n');
                    dict.TryAdd("total_lines", lines);
                }
            }
            catch
            {
                // ignore
            }
        }

        return dict;
    }

    private string? TryWriteRawToolResult(string rawJson)
    {
        try
        {
            if (_store is not Agent.Harness.Persistence.JsonlSessionStore jsonl)
                return null;

            var threadDir = Path.Combine(jsonl.RootDir, _sessionId, "threads", _threadId);
            var outDir = Path.Combine(threadDir, "raw_tool_results");
            Directory.CreateDirectory(outDir);

            var path = Path.Combine(outDir, $"tool-result-{Guid.NewGuid():N}.json");
            File.WriteAllText(path, rawJson);
            return path;
        }
        catch
        {
            return null;
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

            var meaiMessages = Agent.Harness.Llm.MeaiPromptRenderer.Render(
                state,
                compactionTailMessageCount: _compactionTailMessageCount,
                maxTailMessageChars: _compactionMaxTailMessageChars);

            // Session metadata system prompt (client-/protocol-agnostic).
            var meta = _store?.TryLoadMetadata(_sessionId);
            var threadMeta = _threadStore?.TryLoadThreadMetadata(_sessionId, _threadId);

            var toolsForThread = state.Tools;
            if (_threadStore is not null)
                toolsForThread = Agent.Harness.Threads.ThreadCapabilitiesEvaluator.FilterToolsForThread(_sessionId, _threadId, state.Tools, _threadStore);


            var ctx = new SystemPromptContext(
                SessionId: _sessionId,
                SessionMetadata: meta,
                ModelCatalogPrompt: _modelCatalogSystemPrompt,
                ThreadId: _threadId,
                ThreadMetadata: threadMeta,
                OfferedToolNames: toolsForThread.Select(t => t.Name).ToImmutableHashSet(StringComparer.Ordinal));

            // Stable, deterministic order (provider prefix-cache friendly).
            var fragments = _systemPromptComposer.Compose(ctx);
            for (var i = fragments.Count - 1; i >= 0; i--)
                meaiMessages.Insert(0, new MeaiChatMessage(MeaiChatRole.System, fragments[i].Content));


        var options = new Microsoft.Extensions.AI.ChatOptions
        {
            ToolMode = Microsoft.Extensions.AI.ChatToolMode.Auto,
            AllowMultipleToolCalls = true,
            Tools = new List<Microsoft.Extensions.AI.AITool>(),
        };

        // Optional per-provider/model output cap.
        // Prefer thread model (if set) otherwise session model.
        var friendlyModel = string.IsNullOrWhiteSpace(threadMeta?.Model) ? state.Model : threadMeta!.Model;
        var maxOut = _maxOutputTokensByFriendlyName?.Invoke(friendlyModel);
        if (maxOut is > 0)
            options.MaxOutputTokens = maxOut.Value;

        foreach (var t in toolsForThread)
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

        var providerModel = _providerModelByFriendlyName?.Invoke(call.Model);

        var chat = _chatByModel is null ? _chat : _chatByModel(call.Model);
        var updates = chat.GetStreamingResponseAsync(meaiMessages, options, cancellationToken);

        await foreach (var o in MeaiObservedEventSource.FromStreamingResponse(updates, cancellationToken).ConfigureAwait(false))
        {
            if (o is ObservedTokenUsage u && !string.IsNullOrWhiteSpace(providerModel))
                yield return u with { ProviderModel = providerModel };
            else
                yield return o;
        }
        }
        finally
        {
        }
    }

    private const string CompactionSystemPrompt =
        "You are a session compactor. You will be given a transcript of conversation + tool activity.\n" +
        "Your job is to produce high-quality memory so the agent can continue work.\n\n" +
        "OUTPUT FORMAT (REQUIRED):\n" +
        "- Return EXACTLY ONE <compaction>...</compaction> block and nothing else.\n" +
        "- Use markdown headings inside the block, in this exact order:\n" +
        "  1) ## Overview\n" +
        "  2) ## Intent\n" +
        "  3) ## Actions\n" +
        "  4) ## Decisions\n" +
        "  5) ## Important facts + details\n" +
        "  6) ## Open questions\n" +
        "  7) ## Next steps\n\n" +
        "QUALITY RULES:\n" +
        "- Focus on intent, actions, outcomes, artifacts, constraints, and what to do next.\n" +
        "- Do NOT include tool call ids.\n" +
        "- Do NOT paste raw tool output bodies. Summarize them.\n" +
        "- If tool args are huge, summarize rather than repeating them verbatim.\n\n" +
        "GOOD EXAMPLE (shape only):\n" +
        "<compaction>\n" +
        "## Overview\n" +
        "...\n\n" +
        "## Intent\n" +
        "- primary: ...\n\n" +
        "## Actions\n" +
        "- Did X (purpose: Y). Outcome: success/failure.\n\n" +
        "## Decisions\n" +
        "- ...\n\n" +
        "## Important facts + details\n" +
        "- ...\n\n" +
        "## Open questions\n" +
        "- ...\n\n" +
        "## Next steps\n" +
        "1. ...\n" +
        "</compaction>";

    private static string ResolveModelFriendly(SessionState state)
    {
        var last = state.Committed.OfType<SetModel>().LastOrDefault();
        return last?.Model ?? "default";
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
