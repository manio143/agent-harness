using System.Collections.Immutable;

namespace Agent.Harness;

public static partial class TurnRunner
{
    public readonly record struct ReduceStep(
        SessionState Next,
        ImmutableArray<SessionEvent> NewlyCommitted,
        ImmutableArray<Effect> Effects);

    public static ReduceStep ReduceOne(SessionState state, ObservedChatEvent observed, CoreOptions? options = null)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        if (observed is null) throw new ArgumentNullException(nameof(observed));

        var reduced = Core.Reduce(state, observed, options);
        return new ReduceStep(reduced.Next, reduced.NewlyCommitted, reduced.Effects);
    }
}
