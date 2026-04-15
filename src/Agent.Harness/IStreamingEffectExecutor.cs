namespace Agent.Harness;

/// <summary>
/// Optional streaming extension for effect execution.
///
/// Motivation:
/// - CallModel must be streamed so deltas can be committed/published live.
/// - BUT we must not execute other effects (tools/permissions) until the model stream finishes.
///
/// TurnRunner will prefer this interface for CallModel when available.
/// </summary>
public interface IStreamingEffectExecutor : IEffectExecutor
{
    IAsyncEnumerable<ObservedChatEvent> ExecuteStreamingAsync(
        SessionState state,
        Effect effect,
        CancellationToken cancellationToken);
}
