using System.Collections.Immutable;

namespace Agent.Harness;

/// <summary>
/// Imperative shell helper: consumes a stream of observed events and emits only committed events.
///
/// This is the "commit gate": downstream adapters (ACP/UI) should publish only what the core commits.
/// </summary>
public static partial class TurnRunner
{
    public static async IAsyncEnumerable<SessionEvent> RunAsync(
        SessionState initial,
        IAsyncEnumerable<ObservedChatEvent> observed,
        CoreOptions? options = null,
        Action<SessionState>? onState = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (initial is null) throw new ArgumentNullException(nameof(initial));
        if (observed is null) throw new ArgumentNullException(nameof(observed));

        var state = initial;
        onState?.Invoke(state);

        var currentThreadId = Agent.Harness.Threads.ThreadIds.Main;

        await foreach (var evt in observed.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (evt is ObservedTurnStarted ts)
                currentThreadId = ts.ThreadId;

            var step = ReduceOne(state, evt, options);
            state = step.Next;
            onState?.Invoke(state);

            foreach (var committed in step.NewlyCommitted)
                yield return committed;
        }

        // Give the reducer a chance to end the turn cleanly (TurnEnded) once the observed stream ends.
        // This keeps RunAsync aligned with RunWithEffectsAsync for the common "single turn" cases.
        if (state.Committed.IsDefaultOrEmpty || state.Committed[^1] is not TurnEnded)
        {
            var step = ReduceOne(state, new ObservedTurnStabilized(currentThreadId), options);
            state = step.Next;
            onState?.Invoke(state);

            foreach (var committed in step.NewlyCommitted)
                yield return committed;
        }
    }
}
