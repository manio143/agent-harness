using System.Collections.Immutable;
using System.Text.Json;

namespace Agent.Harness.Tools.Handlers;

using Agent.Harness.Threads;

public sealed class ThreadListToolHandler(IThreadTools? threadTools) : IToolHandler
{
    public static ToolDefinition Definition { get; } = new(
        Name: "thread_list",
        Description: "List threads in the current session, including their current intent, status, and model.",
        InputSchema: ParseSchema("""
        { "type": "object", "properties": { }, "required": [ ] }
        """));

    ToolDefinition IToolHandler.Definition => Definition;

    public Task<ImmutableArray<ObservedChatEvent>> ExecuteAsync(SessionState state, ExecuteToolCall tool, CancellationToken cancellationToken)
    {
        var threads = threadTools?.List() ?? ImmutableArray<ThreadInfo>.Empty;
        return Task.FromResult(ImmutableArray.Create<ObservedChatEvent>(
            new ObservedToolCallCompleted(tool.ToolId, JsonSerializer.SerializeToElement(new { threads }))));
    }

    private static JsonElement ParseSchema(string json) => JsonDocument.Parse(json).RootElement;
}
