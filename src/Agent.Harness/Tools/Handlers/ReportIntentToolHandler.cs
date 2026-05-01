using System.Collections.Immutable;
using System.Text.Json;

namespace Agent.Harness.Tools.Handlers;

using Agent.Harness.Threads;

public sealed class ReportIntentToolHandler(
    IThreadTools? threadTools,
    string threadId,
    Agent.Harness.Llm.CommandSuggestions.ICommandIntentSuggester? suggester = null) : IToolHandler
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

        var s = suggester ?? Agent.Harness.Llm.CommandSuggestions.NullCommandIntentSuggester.Instance;
        // Best-effort suggestion: never fail report_intent due to model/suggestion issues.
        ImmutableArray<Agent.Harness.Llm.CommandSuggestions.CommandSuggestion> suggestions;
        try
        {
            suggestions = s.SuggestAsync(intent, state.Tools, cancellationToken).GetAwaiter().GetResult();
        }
        catch
        {
            suggestions = ImmutableArray<Agent.Harness.Llm.CommandSuggestions.CommandSuggestion>.Empty;
        }

        var payload = JsonSerializer.SerializeToElement(new
        {
            ok = true,
            suggestedCommands = suggestions.Select(x => new { name = x.Name, reason = x.Reason }).ToArray()
        });

        return Task.FromResult(ImmutableArray.Create<ObservedChatEvent>(
            new ObservedToolCallCompleted(tool.ToolId, payload)));
    }

    private static JsonElement ParseSchema(string json) => JsonDocument.Parse(json).RootElement;
}
