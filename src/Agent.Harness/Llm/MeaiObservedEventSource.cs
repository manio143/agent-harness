using Microsoft.Extensions.AI;

namespace Agent.Harness.Llm;

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
        var assistantMessageOpen = false;

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
                        {
                            if (!string.IsNullOrEmpty(tc.Text))
                            {
                                assistantMessageOpen = true;
                                yield return new Agent.Harness.ObservedAssistantTextDelta(tc.Text) { RawUpdate = u };
                            }
                            break;
                        }

                        case TextReasoningContent rc:
                        {
                            // Boundary: switching to reasoning closes any open assistant message.
                            if (assistantMessageOpen)
                            {
                                assistantMessageOpen = false;
                                yield return new Agent.Harness.ObservedAssistantMessageCompleted(null) { RawUpdate = u };
                            }

                            if (!string.IsNullOrEmpty(rc.Text))
                                yield return new Agent.Harness.ObservedReasoningTextDelta(rc.Text) { RawUpdate = u };
                            break;
                        }

                        // Mode A: tool-call intent is surfaced as a FunctionCallContent in the model stream.
                        case FunctionCallContent:
                        {
                            // Boundary: tool calls may arrive after assistant text in the same stream.
                            // Invariant: close the assistant message before emitting tool call intent.
                            if (assistantMessageOpen)
                            {
                                assistantMessageOpen = false;
                                yield return new Agent.Harness.ObservedAssistantMessageCompleted(null) { RawUpdate = u };
                            }

                            foreach (var evt in MeaiToolCallParser.Parse(u))
                                yield return evt;
                            break;
                        }
                    }
                }
            }
            else if (!string.IsNullOrEmpty(u.Text))
            {
                // Fallback to convenience text property.
                assistantMessageOpen = true;
                yield return new Agent.Harness.ObservedAssistantTextDelta(u.Text) { RawUpdate = u };
            }

            // Boundary: if MEAI signals finish, emit a completion observed event.
            if (u.FinishReason is not null)
            {
                assistantMessageOpen = false;
                yield return new Agent.Harness.ObservedAssistantMessageCompleted(u.FinishReason.ToString()) { RawUpdate = u };
            }
        }

        // Hard boundary: stream completed.
        // If assistant text was open, close it.
        if (assistantMessageOpen)
            yield return new Agent.Harness.ObservedAssistantMessageCompleted(null) { RawUpdate = null };

        // Always emit a final completion marker so the reducer has a deterministic boundary.
        yield return new Agent.Harness.ObservedAssistantMessageCompleted(null) { RawUpdate = null };
    }
}
