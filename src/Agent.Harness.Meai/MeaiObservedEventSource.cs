using Microsoft.Extensions.AI;

namespace Agent.Harness.Meai;

/// <summary>
/// Normalizes MEAI streaming updates into harness observed events.
///
/// This is intentionally small and lossless: we attach the raw update to each observed event.
/// </summary>
public static class MeaiObservedEventSource
{
    public static async IAsyncEnumerable<Agent.Harness.ObservedChatEvent> FromStreamingResponse(
        IAsyncEnumerable<ChatResponseUpdate> updates,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var u in updates.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            // Prefer structured contents when present.
            if (u.Contents is { Count: > 0 })
            {
                foreach (var c in u.Contents)
                {
                    switch (c)
                    {
                        case TextContent tc:
                            if (!string.IsNullOrEmpty(tc.Text))
                                yield return new Agent.Harness.ObservedAssistantTextDelta(tc.Text) { RawUpdate = u };
                            break;

                        case TextReasoningContent rc:
                            if (!string.IsNullOrEmpty(rc.Text))
                                yield return new Agent.Harness.ObservedReasoningTextDelta(rc.Text) { RawUpdate = u };
                            break;
                    }
                }
            }
            else if (!string.IsNullOrEmpty(u.Text))
            {
                // Fallback to convenience text property.
                yield return new Agent.Harness.ObservedAssistantTextDelta(u.Text) { RawUpdate = u };
            }

            // Boundary: if MEAI signals finish, emit a completion observed event.
            if (u.FinishReason is not null)
                yield return new Agent.Harness.ObservedAssistantMessageCompleted(u.FinishReason.ToString()) { RawUpdate = u };
        }

        // Hard boundary: stream completed.
        yield return new Agent.Harness.ObservedAssistantMessageCompleted(null) { RawUpdate = null };
    }
}
