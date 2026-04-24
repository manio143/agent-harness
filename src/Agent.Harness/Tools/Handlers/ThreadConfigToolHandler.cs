using System.Collections.Immutable;
using System.Text.Json;

namespace Agent.Harness.Tools.Handlers;

using Agent.Harness.Threads;

public sealed class ThreadConfigToolHandler(
    IThreadTools? threadTools,
    IThreadLifecycle? lifecycle,
    string currentThreadId,
    Func<string, bool>? isKnownModel) : IToolHandler
{
    public static ToolDefinition Definition { get; } = new(
        Name: "thread_config",
        Description: "Get or set thread configuration (currently: model).",
        InputSchema: ParseSchema("""
        {
          "type": "object",
          "properties": {
            "threadId": { "type": "string", "description": "Thread id (defaults to current thread)" },
            "model": { "type": "string", "description": "Model friendly name to use for this thread (or 'default')" }
          },
          "required": [ ]
        }
        """));

    ToolDefinition IToolHandler.Definition => Definition;

    public async Task<ImmutableArray<ObservedChatEvent>> ExecuteAsync(SessionState state, ExecuteToolCall tool, CancellationToken cancellationToken)
    {
        var args = Agent.Harness.Tools.ToolArgs.Normalize(tool.Args);

        var targetThreadId = args.TryGetValue("threadId", out var tidVal) && tidVal.ValueKind == JsonValueKind.String
            ? tidVal.GetString()!
            : currentThreadId;

        // Read-only: return current projected model.
        if (!args.TryGetValue("model", out var modelVal) || modelVal.ValueKind != JsonValueKind.String)
        {
            var current = targetThreadId == currentThreadId
                ? ResolveModelFromCommitted(state.Committed)
                : threadTools?.GetModel(targetThreadId) ?? "default";

            return ImmutableArray.Create<ObservedChatEvent>(
                new ObservedToolCallCompleted(tool.ToolId, JsonSerializer.SerializeToElement(new { threadId = targetThreadId, model = current })));
        }

        var model = modelVal.GetString()!;
        if (string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException("thread_config.model_required");

        if (!string.Equals(model, "default", StringComparison.OrdinalIgnoreCase)
            && isKnownModel is not null
            && !isKnownModel(model))
        {
            throw new InvalidOperationException("thread_config.unknown_model");
        }

        // Cross-thread: persist immediately via orchestrator lifecycle.
        if (targetThreadId != currentThreadId)
        {
            if (lifecycle is null)
                throw new InvalidOperationException("thread_tools_require_orchestrator");

            await lifecycle.RequestSetThreadModelAsync(targetThreadId, model, cancellationToken).ConfigureAwait(false);

            return ImmutableArray.Create<ObservedChatEvent>(
                new ObservedToolCallCompleted(tool.ToolId, JsonSerializer.SerializeToElement(new { ok = true, threadId = targetThreadId, model })));
        }

        return ImmutableArray.Create<ObservedChatEvent>(
            new ObservedSetModel(targetThreadId, model),
            new ObservedToolCallCompleted(tool.ToolId, JsonSerializer.SerializeToElement(new { ok = true, threadId = targetThreadId, model })));
    }

    private static string ResolveModelFromCommitted(ImmutableArray<SessionEvent> committed)
        => committed.OfType<SetModel>().Select(m => m.Model).LastOrDefault() ?? "default";

    private static JsonElement ParseSchema(string json)
        => JsonDocument.Parse(json).RootElement.Clone();
}
