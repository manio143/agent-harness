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

        // Scheduling strategy (important invariants):
        // - We fully DRAIN the current observed batch through the reducer BEFORE executing any effects.
        // - "Long" effects like CallModel must be treated as batch boundaries: we run CallModel,
        //   wait for its streaming to finish (ExecuteAsync returns only after stream completes),
        //   then reduce ALL produced observations before executing tool effects.
        // This avoids interleaving tool execution with an in-flight model stream.

        var state = initial;
        onState?.Invoke(state);

        sink ??= NullEventSink.Instance;

        var observedQueue = new Queue<ObservedChatEvent>();
        var pendingEffects = new List<Effect>();

        await using var enumerator = observed.GetAsyncEnumerator(cancellationToken);
        var externalDone = false;

        async Task<bool> TryEnqueueNextExternalAsync()
        {
            if (externalDone) return false;
            if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
            {
                externalDone = true;
                return false;
            }

            observedQueue.Enqueue(enumerator.Current);
            return true;
        }

        while (true)
        {
            // 1) If we have nothing to reduce, pull the next external observation (if any).
            if (observedQueue.Count == 0)
            {
                await TryEnqueueNextExternalAsync().ConfigureAwait(false);
            }

            // 2) Reduce the entire observed queue (a "batch") before running effects.
            while (observedQueue.Count > 0)
            {
                var next = observedQueue.Dequeue();

                await sink.OnObservedAsync(next, cancellationToken).ConfigureAwait(false);

                var step = ReduceOne(state, next, options);
                state = step.Next;
                onState?.Invoke(state);

                foreach (var committed in step.NewlyCommitted)
                {
                    await sink.OnCommittedAsync(committed, cancellationToken).ConfigureAwait(false);
                    yield return committed;
                }

                if (!step.Effects.IsDefaultOrEmpty)
                    pendingEffects.AddRange(step.Effects);
            }

            // 3) If no pending effects, and no more external observations, we're done.
            if (pendingEffects.Count == 0)
            {
                if (externalDone)
                    break;

                // Otherwise loop back to pull more external observed items.
                continue;
            }

            // 4) Execute effects in phases.
            // Phase 1: CallModel (at most once per phase, deduped).
            var callModel = pendingEffects.OfType<CallModel>().FirstOrDefault();
            if (callModel is not null)
            {
                // Remove ALL CallModel requests; one model call is enough.
                pendingEffects.RemoveAll(e => e is CallModel);

                if (effects is IStreamingEffectExecutor streaming)
                {
                    // Stream model observations through the reducer immediately so deltas can be
                    // committed/published live. Do NOT execute other effects until the model stream ends.
                    await foreach (var obs in streaming.ExecuteStreamingAsync(state, callModel, cancellationToken).ConfigureAwait(false))
                    {
                        await sink.OnObservedAsync(obs, cancellationToken).ConfigureAwait(false);

                        var step = ReduceOne(state, obs, options);
                        state = step.Next;
                        onState?.Invoke(state);

                        foreach (var committed in step.NewlyCommitted)
                        {
                            await sink.OnCommittedAsync(committed, cancellationToken).ConfigureAwait(false);
                            yield return committed;
                        }

                        if (!step.Effects.IsDefaultOrEmpty)
                            pendingEffects.AddRange(step.Effects);
                    }

                    // Now that the model stream has completed, loop back to drain any queued observations
                    // and/or execute the resulting tool effects.
                    continue;
                }
                else
                {
                    // Non-streaming fallback: buffer observations.
                    var observations = await effects.ExecuteAsync(state, callModel, cancellationToken).ConfigureAwait(false);
                    foreach (var obs in observations)
                        observedQueue.Enqueue(obs);

                    // Important: we must now drain these observations through the reducer BEFORE executing
                    // any other effects (permissions/tools). So we continue the outer loop.
                    continue;
                }
            }

            // Phase 2: all other effects (deduped) after the model stream is fully reduced.
            var batch = DeduplicateEffects(pendingEffects.ToImmutableArray());
            pendingEffects.Clear();

            foreach (var eff in batch)
            {
                var observations = await effects.ExecuteAsync(state, eff, cancellationToken).ConfigureAwait(false);
                foreach (var obs in observations)
                    observedQueue.Enqueue(obs);
            }
        }
    }
}
