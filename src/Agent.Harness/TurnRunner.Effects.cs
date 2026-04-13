using System.Collections.Immutable;

namespace Agent.Harness;

public static partial class TurnRunner
{
    public static async IAsyncEnumerable<SessionEvent> RunWithEffectsAsync(
        SessionState initial,
        IAsyncEnumerable<ObservedChatEvent> observed,
        IEffectExecutor effects,
        CoreOptions? options = null,
        Action<SessionState>? onState = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (initial is null) throw new ArgumentNullException(nameof(initial));
        if (observed is null) throw new ArgumentNullException(nameof(observed));
        if (effects is null) throw new ArgumentNullException(nameof(effects));

        // Merge strategy:
        // - external observed stream is consumed in order
        // - effects produce internal observations that are processed before pulling more external items
        // This keeps the loop deterministic and avoids channel bookkeeping.

        var state = initial;
        onState?.Invoke(state);

        var internalQueue = new Queue<ObservedChatEvent>();

        await using var enumerator = observed.GetAsyncEnumerator(cancellationToken);

        while (true)
        {
            ObservedChatEvent next;

            if (internalQueue.Count > 0)
            {
                next = internalQueue.Dequeue();
            }
            else
            {
                if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    break;

                next = enumerator.Current;
            }

            var reduced = Core.Reduce(state, next, options);
            state = reduced.Next;
            onState?.Invoke(state);

            foreach (var committed in reduced.NewlyCommitted)
                yield return committed;

            // Execute effects sequentially in emission order, enqueue resulting observations.
            foreach (var eff in reduced.Effects)
            {
                var observations = await effects.ExecuteAsync(state, eff, cancellationToken).ConfigureAwait(false);
                foreach (var obs in observations)
                    internalQueue.Enqueue(obs);
            }
        }
    }
}
