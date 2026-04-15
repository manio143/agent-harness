using System.Collections.Immutable;

namespace Agent.Harness.Threads;

public static class ThreadStatusProjector
{
    public static ThreadStatus ProjectStatus(ImmutableArray<SessionEvent> committed)
    {
        var lastStarted = -1;
        var lastEnded = -1;

        for (var i = 0; i < committed.Length; i++)
        {
            switch (committed[i])
            {
                case TurnStarted:
                    lastStarted = i;
                    break;
                case TurnEnded:
                    lastEnded = i;
                    break;
            }
        }

        if (lastStarted < 0)
            return ThreadStatus.Idle;

        return lastEnded > lastStarted ? ThreadStatus.Idle : ThreadStatus.Running;
    }

    public static bool IsIdle(ImmutableArray<SessionEvent> committed) => ProjectStatus(committed) == ThreadStatus.Idle;

    /// <summary>
    /// Determines whether the thread should be treated as idle at a wake boundary.
    /// SessionRunner prepends TurnStarted before emitting wake events; for wake-time gating
    /// we should ignore that leading marker.
    /// </summary>
    public static bool IsIdleAtWakeBoundary(ImmutableArray<SessionEvent> committed)
    {
        var view = committed;
        if (!view.IsDefaultOrEmpty && view[^1] is TurnStarted)
            view = view.RemoveAt(view.Length - 1);

        return IsIdle(view);
    }
}
