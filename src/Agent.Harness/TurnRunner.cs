using System.Collections.Immutable;

namespace Agent.Harness;

/// <summary>
/// Imperative shell helper: consumes a stream of observed events and emits only committed events.
///
/// This is the "commit gate": downstream adapters (ACP/UI) should publish only what the core commits.
/// </summary>
public static class TurnRunner
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

        await foreach (var evt in observed.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var reduced = Core.Reduce(state, evt, options);
            state = reduced.Next;
            onState?.Invoke(state);

            foreach (var committed in reduced.NewlyCommitted)
            {
                yield return committed;
            }
        }
    }
}
