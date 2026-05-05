using System.Collections.Immutable;
using System.Diagnostics;

namespace Agent.Harness;

internal static class TurnInvariants
{
    [Conditional("DEBUG")]
    public static void AssertNoOpenToolCallsAtTurnEnd(ImmutableArray<SessionEvent> committed)
    {
        // Find the most recent TurnStarted..TurnEnded slice.
        var end = LastIndexOf<TurnEnded>(committed);
        if (end < 0) return;

        var start = LastIndexOf<TurnStarted>(committed, end);
        if (start < 0) return;

        var turn = committed[start..(end + 1)];

        foreach (var r in turn.OfType<ToolCallRequested>())
        {
            // report_intent is a meta-tool; it may be emitted by the model even when the harness rejects
            // subsequent tool calls early (e.g. invalid_args). Treat it as non-blocking for this invariant.
            if (r.ToolName == ToolSchemas.ReportIntent.Name)
                continue;

            if (!HasTerminalToolCall(turn, r.ToolId))
                Debug.Fail($"turn_invariant_failed: open tool call at TurnEnded: toolId={r.ToolId} toolName={r.ToolName}");
        }
    }

    private static bool HasTerminalToolCall(ImmutableArray<SessionEvent> committed, string toolId)
        => committed.Any(e => e switch
        {
            ToolCallCompleted c when c.ToolId == toolId => true,
            ToolCallFailed f when f.ToolId == toolId => true,
            ToolCallCancelled c when c.ToolId == toolId => true,
            ToolCallRejected r when r.ToolId == toolId => true,
            _ => false,
        });

    private static int LastIndexOf<T>(ImmutableArray<SessionEvent> events)
        where T : SessionEvent
        => LastIndexOf<T>(events, events.Length - 1);

    private static int LastIndexOf<T>(ImmutableArray<SessionEvent> events, int fromInclusive)
        where T : SessionEvent
    {
        for (var i = Math.Min(fromInclusive, events.Length - 1); i >= 0; i--)
            if (events[i] is T) return i;
        return -1;
    }
}
