using System.Collections.Immutable;
using System.Text.Json;

namespace Agent.Harness.Tools.Handlers;

using Agent.Harness.Threads;

public sealed class ReportIntentToolHandler(IThreadTools? threadTools, string threadId) : IToolHandler
{
    public static ToolDefinition Definition { get; } = new(
        Name: "report_intent",
        Description: "Report the thread's current intent (short, single sentence). Must be called before any other tools in the same turn. IMPORTANT: call this as a tool/function call (do NOT output <report_intent>...</report_intent> in plain text).",
        InputSchema: ParseSchema("""
        {
          "type": "object",
          "properties": {
            "intent": { "type": "string", "description": "Short sentence describing what you are trying to do" }
          },
          "required": ["intent"]
        }
        """));

    ToolDefinition IToolHandler.Definition => Definition;

    public Task<ImmutableArray<ObservedChatEvent>> ExecuteAsync(SessionState state, ExecuteToolCall tool, CancellationToken cancellationToken)
    {
        var args = Agent.Harness.Tools.ToolArgs.Normalize(tool.Args);
        var intent = args.TryGetValue("intent", out var intentVal) && intentVal.ValueKind == JsonValueKind.String
            ? intentVal.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(intent))
            throw new InvalidOperationException("report_intent.intent_required");

        threadTools?.ReportIntent(threadId, intent);

        return Task.FromResult(ImmutableArray.Create<ObservedChatEvent>(
            new ObservedToolCallCompleted(tool.ToolId, JsonSerializer.SerializeToElement(new { ok = true }))));
    }

    private static JsonElement ParseSchema(string json) => JsonDocument.Parse(json).RootElement;
}
