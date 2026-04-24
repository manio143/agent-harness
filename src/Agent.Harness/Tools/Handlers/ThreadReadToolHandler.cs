using System.Collections.Immutable;
using System.Text.Json;

namespace Agent.Harness.Tools.Handlers;

using Agent.Harness.Threads;

public sealed class ThreadReadToolHandler(IThreadTools? threadTools) : IToolHandler
{
    public static ToolDefinition Definition { get; } = new(
        Name: "thread_read",
        Description: "Read assistant messages from another thread.",
        InputSchema: ParseSchema("""
        {
          "type": "object",
          "properties": {
            "threadId": { "type": "string", "description": "Thread id to read from" }
          },
          "required": ["threadId"]
        }
        """));

    ToolDefinition IToolHandler.Definition => Definition;

    public Task<ImmutableArray<ObservedChatEvent>> ExecuteAsync(SessionState state, ExecuteToolCall tool, CancellationToken cancellationToken)
    {
        var args = Agent.Harness.Tools.ToolArgs.Normalize(tool.Args);
        if (!args.TryGetValue("threadId", out var tidVal) || tidVal.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("thread_read.threadId_required");

        var threadId = tidVal.GetString();
        if (string.IsNullOrWhiteSpace(threadId))
            throw new InvalidOperationException("thread_read.threadId_required");

        var messages = threadTools?.ReadThreadMessages(threadId) ?? ImmutableArray<ThreadMessage>.Empty;
        return Task.FromResult(ImmutableArray.Create<ObservedChatEvent>(
            new ObservedToolCallCompleted(tool.ToolId, JsonSerializer.SerializeToElement(new { messages }))));
    }

    private static JsonElement ParseSchema(string json) => JsonDocument.Parse(json).RootElement;
}
