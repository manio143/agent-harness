using System.Collections.Immutable;
using System.Text.Json;

namespace Agent.Harness.Tools.Handlers;

using Agent.Harness.Threads;

public sealed class ThreadConfigToolHandler(
    IThreadLifecycle lifecycle,
    string currentThreadId,
    Func<string, bool> isKnownModel) : IToolHandler
{
    public static ToolDefinition Definition { get; } = new(
        Name: "thread_config",
        Description: "Configure a thread. Currently supports setting the model.",
        InputSchema: ParseSchema("""
        {
          "type": "object",
          "properties": {
            "threadId": { "type": "string", "description": "Thread id to configure (defaults to current thread)" },
            "model": { "type": "string", "description": "Model friendly name (or 'default')" }
          },
          "required": ["model"]
        }
        """));

    ToolDefinition IToolHandler.Definition => Definition;

    public async Task<ImmutableArray<ObservedChatEvent>> ExecuteAsync(SessionState state, ExecuteToolCall tool, CancellationToken cancellationToken)
    {
        var args = Agent.Harness.Tools.ToolArgs.Normalize(tool.Args);

        var targetThreadId = currentThreadId;
        if (args.TryGetValue("threadId", out var tidVal) && tidVal.ValueKind == JsonValueKind.String)
        {
            var tid = tidVal.GetString();
            if (!string.IsNullOrWhiteSpace(tid))
                targetThreadId = tid;
        }

        if (!args.TryGetValue("model", out var modelVal) || modelVal.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("thread_config.model_required");

        var model = modelVal.GetString();
        if (string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException("thread_config.model_required");

        if (!isKnownModel(model))
            throw new InvalidOperationException($"thread_config.unknown_model:{model}");

        await lifecycle.RequestSetThreadModelAsync(targetThreadId, model, cancellationToken).ConfigureAwait(false);

        return ImmutableArray.Create<ObservedChatEvent>(
            new ObservedToolCallCompleted(tool.ToolId, JsonSerializer.SerializeToElement(new { ok = true })));
    }

    private static JsonElement ParseSchema(string json) => JsonDocument.Parse(json).RootElement;
}
