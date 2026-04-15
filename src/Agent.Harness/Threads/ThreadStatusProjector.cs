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
}
