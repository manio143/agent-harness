using System.Collections.Immutable;

namespace Agent.Harness;

/// <summary>
/// Executes Effects emitted by the functional core.
///
/// Invariant: the reducer never executes I/O. It emits Effects; the imperative shell executes them
/// and feeds resulting observations back into the reducer.
/// </summary>
public interface IEffectExecutor
{
    Task<ImmutableArray<ObservedChatEvent>> ExecuteAsync(
        SessionState state,
        Effect effect,
        CancellationToken cancellationToken);
}
