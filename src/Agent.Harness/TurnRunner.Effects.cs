using System.Collections.Immutable;

namespace Agent.Harness;

public static partial class TurnRunner
{
    public static ImmutableArray<Effect> DeduplicateEffects(ImmutableArray<Effect> effects)
    {
        if (effects.IsDefaultOrEmpty) return ImmutableArray<Effect>.Empty;

        var seen = new HashSet<string>();
        var builder = ImmutableArray.CreateBuilder<Effect>();

        foreach (var e in effects)
        {
            var key = e switch
            {
                CheckPermission p => $"check_permission:{p.ToolId}",
                ExecuteToolCall t => $"execute_tool:{t.ToolId}",
                CallModel => "call_model",
                _ => e.GetType().FullName ?? e.GetType().Name,
            };

            if (seen.Add(key))
                builder.Add(e);
        }

        return builder.ToImmutable();
    }

    public static async IAsyncEnumerable<SessionEvent> RunWithEffectsAsync(
        SessionState initial,
        IAsyncEnumerable<ObservedChatEvent> observed,
        IEffectExecutor effects,
        IEventSink? sink = null,
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

        sink ??= NullEventSink.Instance;

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

            await sink.OnObservedAsync(next, cancellationToken).ConfigureAwait(false);

            var step = ReduceOne(state, next, options);
            state = step.Next;
            onState?.Invoke(state);

            foreach (var committed in step.NewlyCommitted)
            {
                await sink.OnCommittedAsync(committed, cancellationToken).ConfigureAwait(false);
                yield return committed;
            }

            // Execute effects in batches. We deduplicate within the batch to avoid scheduling the
            // same "long" effect (e.g. CallModel) multiple times before prior output is processed.
            // This keeps the loop deterministic and prevents overlapping model streams.
            var batch = DeduplicateEffects(step.Effects);

            foreach (var eff in batch)
            {
                var observations = await effects.ExecuteAsync(state, eff, cancellationToken).ConfigureAwait(false);
                foreach (var obs in observations)
                    internalQueue.Enqueue(obs);
            }
        }
    }
}
