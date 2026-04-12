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

    public SessionRunner(CoreOptions coreOptions, SessionTitleGenerator titleGenerator)
    {
        _coreOptions = coreOptions;
        _titleGenerator = titleGenerator;
    }

    public async Task<SessionRunnerResult> RunTurnAsync(
        SessionState initial,
        IAsyncEnumerable<ObservedChatEvent> observed,
        CancellationToken cancellationToken)
    {
        var newly = ImmutableArray.CreateBuilder<SessionEvent>();
        var state = initial;

        await foreach (var committed in TurnRunner.RunAsync(
            initial,
            observed,
            options: _coreOptions,
            onState: s => state = s,
            cancellationToken: cancellationToken))
        {
            newly.Add(committed);
        }

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
