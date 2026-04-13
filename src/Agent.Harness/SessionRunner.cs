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
        SessionState initial,
        IAsyncEnumerable<ObservedChatEvent> observed,
        CancellationToken cancellationToken)
    {
        var newly = ImmutableArray.CreateBuilder<SessionEvent>();
        var state = initial;

        async IAsyncEnumerable<ObservedChatEvent> WithTurnMarkers()
        {
            yield return new ObservedTurnStarted();

            await foreach (var o in observed.WithCancellation(cancellationToken))
                yield return o;
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
            options: _coreOptions,
            onState: s => state = s,
            cancellationToken: cancellationToken))
        {
            newly.Add(committed);
        }

        // Once the runner has drained all observations + effects, the turn is stable.
        var end = Core.Reduce(state, new ObservedTurnStabilized(), _coreOptions);
        state = end.Next;
        newly.AddRange(end.NewlyCommitted);


        // Post-turn policy: generate a title once (after first assistant message exists).
        var titleEvt = await _titleGenerator.MaybeGenerateAfterTurnAsync(state, cancellationToken).ConfigureAwait(false);
        if (titleEvt is not null)
        {
            state = state with { Committed = state.Committed.Add(titleEvt) };
            newly.Add(titleEvt);
        }

        return new SessionRunnerResult(state, newly.ToImmutable());
    }
}
