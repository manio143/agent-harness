using System.Collections.Immutable;

namespace Agent.Harness;

public sealed class NullEffectExecutor : IStreamingEffectExecutor
{
    public static readonly NullEffectExecutor Instance = new();

    private NullEffectExecutor() { }

    public Task<ImmutableArray<ObservedChatEvent>> ExecuteAsync(
        SessionState state,
        Effect effect,
        CancellationToken cancellationToken)
        => Task.FromResult(ImmutableArray<ObservedChatEvent>.Empty);

    public async IAsyncEnumerable<ObservedChatEvent> ExecuteStreamingAsync(
        SessionState state,
        Effect effect,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield break;
    }
}
