using System.Collections.Immutable;
using System.Text.Json;

namespace Agent.Harness.Tools.Handlers;

using Agent.Harness.Threads;

public sealed class ThreadStopToolHandler(IThreadLifecycle lifecycle, string currentThreadId) : IToolHandler
{
    public static ToolDefinition Definition { get; } = new(
        Name: "thread_stop",
        Description: "Stop/close a thread.",
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

        if (!args.TryGetValue("threadId", out var tidVal) || tidVal.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("thread_stop.threadId_required");

        var threadId = tidVal.GetString();
        if (string.IsNullOrWhiteSpace(threadId))
            throw new InvalidOperationException("thread_stop.threadId_required");

        if (threadId == currentThreadId)
            throw new InvalidOperationException("thread_stop.cannot_stop_current_thread");

        string? reason = null;
        if (args.TryGetValue("reason", out var reasonVal) && reasonVal.ValueKind == JsonValueKind.String)
            reason = reasonVal.GetString();

        await lifecycle.RequestStopThreadAsync(threadId, reason, cancellationToken).ConfigureAwait(false);

        return ImmutableArray.Create<ObservedChatEvent>(
            new ObservedToolCallCompleted(tool.ToolId, JsonSerializer.SerializeToElement(new { ok = true })));
    }

    private static JsonElement ParseSchema(string json) => JsonDocument.Parse(json).RootElement;
}
