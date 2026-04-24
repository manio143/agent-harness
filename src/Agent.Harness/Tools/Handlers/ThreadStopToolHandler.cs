using System.Collections.Immutable;
using System.Text.Json;

namespace Agent.Harness.Tools.Handlers;

using Agent.Harness.Threads;

public sealed class ThreadStopToolHandler(IThreadLifecycle? lifecycle) : IToolHandler
{
    public static ToolDefinition Definition { get; } = new(
        Name: "thread_stop",
        Description: "Stop/close a thread so it can no longer receive messages and is removed from the thread list.",
        InputSchema: ParseSchema("""
        {
          "type": "object",
          "properties": {
            "threadId": { "type": "string", "description": "Thread id to stop" },
            "reason": { "type": "string", "description": "Optional reason for stopping" }
          },
          "required": ["threadId"]
        }
        """));

    ToolDefinition IToolHandler.Definition => Definition;

    public async Task<ImmutableArray<ObservedChatEvent>> ExecuteAsync(SessionState state, ExecuteToolCall tool, CancellationToken cancellationToken)
    {
        var args = Agent.Harness.Tools.ToolArgs.Normalize(tool.Args);

        var threadId = GetRequiredString(args, "threadId");
        var reason = args.TryGetValue("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;

        if (lifecycle is null)
            throw new InvalidOperationException("thread_tools_require_orchestrator");

        await lifecycle.RequestStopThreadAsync(threadId, reason, cancellationToken).ConfigureAwait(false);

        return ImmutableArray.Create<ObservedChatEvent>(
            new ObservedToolCallCompleted(tool.ToolId, JsonSerializer.SerializeToElement(new { ok = true, threadId })));
    }

    private static string GetRequiredString(Dictionary<string, JsonElement> obj, string name)
    {
        if (!obj.TryGetValue(name, out var v) || v.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException($"missing_required:{name}");

        return v.GetString() ?? "";
    }

    private static JsonElement ParseSchema(string json)
        => JsonDocument.Parse(json).RootElement.Clone();
}
