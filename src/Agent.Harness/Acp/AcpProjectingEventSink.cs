using System.Collections.Immutable;

namespace Agent.Harness.Acp;

/// <summary>
/// Event sink that persists committed events via an inner sink, and incrementally projects committed
/// events to ACP emissions via <see cref="AcpProjection"/>.
///
/// This preserves the harness invariant: the committed event log is source of truth, while ACP
/// presentation is a functional projection.
/// </summary>
public sealed class AcpProjectingEventSink : IEventSink
{
    private readonly IEventSink _inner;
    private readonly CoreOptions _coreOptions;
    private readonly AcpPublishOptions _publishOptions;
    private readonly Func<AcpEmission, CancellationToken, ValueTask> _execute;

    public AcpProjectingEventSink(
        IEventSink inner,
        CoreOptions coreOptions,
        AcpPublishOptions publishOptions,
        Func<AcpEmission, CancellationToken, ValueTask> execute)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _coreOptions = coreOptions ?? throw new ArgumentNullException(nameof(coreOptions));
        _publishOptions = publishOptions ?? throw new ArgumentNullException(nameof(publishOptions));
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
    }

    public ValueTask OnObservedAsync(ObservedChatEvent observed, CancellationToken cancellationToken = default)
        => _inner.OnObservedAsync(observed, cancellationToken);

    public async ValueTask OnCommittedAsync(SessionEvent committed, CancellationToken cancellationToken = default)
    {
        await _inner.OnCommittedAsync(committed, cancellationToken).ConfigureAwait(false);

        var emissions = AcpProjection.Project(committed, _coreOptions, _publishOptions);
        foreach (var e in emissions)
            await _execute(e, cancellationToken).ConfigureAwait(false);
    }
}
