using System.Collections.Immutable;
using Agent.Harness.TitleGeneration;

namespace Agent.Harness;

public sealed record SessionRunnerResult(
    SessionState Next,
    ImmutableArray<SessionEvent> NewlyCommitted);

/// <summary>
/// Harness-owned imperative-shell orchestrator.
///
/// Owns session state evolution for a single turn:
/// - observed -> core reducer -> committed
/// - post-turn policies (e.g. title generation) that may commit additional events
/// </summary>
public sealed class SessionRunner
{
    private readonly CoreOptions _coreOptions;
    private readonly SessionTitleGenerator _titleGenerator;
    private readonly IEffectExecutor _effects;

    public SessionRunner(CoreOptions coreOptions, SessionTitleGenerator titleGenerator, IEffectExecutor? effects = null)
    {
        _coreOptions = coreOptions;
        _titleGenerator = titleGenerator;
        _effects = effects ?? NullEffectExecutor.Instance;
    }

    public async Task<SessionRunnerResult> RunTurnAsync(
        string threadId,
        SessionState initial,
        IAsyncEnumerable<ObservedChatEvent> observed,
        CancellationToken cancellationToken,
        IEventSink? sink = null)
    {
        var newly = ImmutableArray.CreateBuilder<SessionEvent>();
        var state = initial;

        async IAsyncEnumerable<ObservedChatEvent> WithTurnMarkers()
        {
            yield return new ObservedTurnStarted(threadId);

            await foreach (var o in observed.WithCancellation(cancellationToken))
                yield return o;

            // Turn stabilization is injected by TurnRunner when the reducer/effects loop would
            // otherwise stop. This ensures TurnEnded cannot be committed while effects (model/tools)
            // are still pending.
        }

        // Run the turn as a reducer/effects loop:
        // - consume observed events
        // - reduce -> commit + effects
        // - execute effects (I/O) -> feed observations back into reducer
        //
        // Invariant: only committed events are returned; effects are never committed.
        await foreach (var committed in TurnRunner.RunWithEffectsAsync(
            initial,
            WithTurnMarkers(),
            effects: _effects,
            sink: sink,
            options: _coreOptions,
            onState: s => state = s,
            cancellationToken: cancellationToken))
        {
            newly.Add(committed);
        }


        // Post-turn policy: generate a title once (after first assistant message exists).
        // Only the MAIN thread is allowed to set the session title.
        if (threadId == Agent.Harness.Threads.ThreadIds.Main)
        {
            var titleEvt = await _titleGenerator.MaybeGenerateAfterTurnAsync(state, cancellationToken).ConfigureAwait(false);
            if (titleEvt is not null)
            {
                // IMPORTANT: title generation happens outside the reducer/effects loop, so we must
                // still run it through the event sink so persistence + projections (session.json title)
                // stay consistent in the threaded model.
                if (sink is not null)
                    await sink.OnCommittedAsync(titleEvt, cancellationToken).ConfigureAwait(false);

                state = state with { Committed = state.Committed.Add(titleEvt) };
                newly.Add(titleEvt);
            }
        }

        return new SessionRunnerResult(state, newly.ToImmutable());
    }
}
