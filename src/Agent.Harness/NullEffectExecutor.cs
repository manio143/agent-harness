using System.Collections.Immutable;

namespace Agent.Harness;

public sealed class NullEffectExecutor : IEffectExecutor
{
    public static readonly NullEffectExecutor Instance = new();

    private NullEffectExecutor() { }

    public Task<ImmutableArray<ObservedChatEvent>> ExecuteAsync(
        SessionState state,
        Effect effect,
        CancellationToken cancellationToken)
        => Task.FromResult(ImmutableArray<ObservedChatEvent>.Empty);
}
