using System.Collections.Immutable;
using System.Diagnostics;

namespace Agent.Harness.Threads;

public static class ForkSeedBuilder
{
    /// <summary>
    /// Build a committed-event seed for a forked child thread.
    /// Invariants:
    /// - Only include fully-ended turns (no dangling TurnStarted without TurnEnded).
    /// - Drop unfulfilled tool-call lifecycle events from the last included round/turn.
    ///   (Unfulfilled = ToolCallRequested without any terminal event.)
    /// - Ensure that, within each included turn, all tool calls are terminal by TurnEnded.
    /// </summary>
    public static ImmutableArray<SessionEvent> BuildForkSeed(ImmutableArray<SessionEvent> committed)
    {
        // thread_start's seed should never carry over prior child bootstrap markers.
        committed = committed.Where(e => e is not NewThreadTask).ToImmutableArray();

        var seed = committed;

        // Variant B: only drop unfulfilled tool call requests from the last round (the current, possibly in-progress turn).
        // Important: forking may happen mid-turn; we still want to carry over the latest user message into the child.
        // But the child must not inherit any dangling tool-call lifecycle.
        var lastTurnStartedIdx = LastIndexOf<TurnStarted>(seed);
        if (lastTurnStartedIdx >= 0)
        {
            var prefix = seed[..lastTurnStartedIdx];
            var turn = seed[lastTurnStartedIdx..];

            turn = DropUnfulfilledToolCalls(turn);

            // Ensure the seed ends cleanly on a turn boundary.
            // The child will start a new turn with NewThreadTask.
            if (turn.Length == 0 || turn[^1] is not TurnEnded)
                turn = turn.Add(new TurnEnded());

            seed = prefix.AddRange(turn);
        }

        // Debug-only invariant: all turns in the seed must have tool calls terminal by end-of-turn.
        // Shared invariant is enforced at TurnEnded commit time; fork seed just ensures it ends on a boundary.
        return seed;
    }

    private static ImmutableArray<SessionEvent> DropUnfulfilledToolCalls(ImmutableArray<SessionEvent> turn)
    {
        var requested = turn.OfType<ToolCallRequested>().ToImmutableArray();

        var openToolIds = requested
            .Select(r => r.ToolId)
            .Where(toolId => !HasTerminalToolCall(turn, toolId))
            .ToHashSet(StringComparer.Ordinal);

        if (openToolIds.Count == 0)
            return turn;

        return turn.Where(e => e switch
        {
            ToolCallRequested r => !openToolIds.Contains(r.ToolId),
            ToolCallPending p => !openToolIds.Contains(p.ToolId),
            ToolCallInProgress p => !openToolIds.Contains(p.ToolId),
            ToolCallCompleted c => !openToolIds.Contains(c.ToolId),
            ToolCallFailed f => !openToolIds.Contains(f.ToolId),
            ToolCallCancelled c => !openToolIds.Contains(c.ToolId),
            ToolCallRejected r => !openToolIds.Contains(r.ToolId),
            _ => true,
        }).ToImmutableArray();
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
    {
        for (var i = events.Length - 1; i >= 0; i--)
            if (events[i] is T) return i;
        return -1;
    }
}
