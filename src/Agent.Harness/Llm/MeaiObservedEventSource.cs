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
        var reasoningMessageOpen = false;

        // Some providers repeat previously-seen tool calls in later streaming updates ("cumulative" deltas)
        // or stream a single tool call over multiple updates.
        // The core reducer deduplicates by ToolId (ToolCallRequested is committed once per toolId),
        // so we must not emit multiple ObservedToolCallDetected events for the same toolId.
        // We buffer the latest intent per toolId and flush once at a turn boundary (finish/end).
        var toolCallOrder = new List<string>();
        var pendingToolCalls = new Dictionary<string, Agent.Harness.ObservedToolCallDetected>(StringComparer.Ordinal);

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
                            // Boundary: switching to assistant text closes any open reasoning message.
                            if (reasoningMessageOpen)
                            {
                                reasoningMessageOpen = false;
                                yield return new Agent.Harness.ObservedReasoningMessageCompleted(null) { RawUpdate = u };
                            }

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
                            {
                                reasoningMessageOpen = true;
                                yield return new Agent.Harness.ObservedReasoningTextDelta(rc.Text) { RawUpdate = u };
                            }
                            break;
                        }

                        // Mode A: tool-call intent is surfaced as a FunctionCallContent in the model stream.
                        case FunctionCallContent:
                        {
                            // Boundary: tool calls may arrive after assistant text or reasoning in the same stream.
                            // Invariant: close the current content-type message before buffering tool call intent.
                            if (assistantMessageOpen)
                            {
                                assistantMessageOpen = false;
                                yield return new Agent.Harness.ObservedAssistantMessageCompleted(null) { RawUpdate = u };
                            }

                            if (reasoningMessageOpen)
                            {
                                reasoningMessageOpen = false;
                                yield return new Agent.Harness.ObservedReasoningMessageCompleted(null) { RawUpdate = u };
                            }

                            foreach (var evt in MeaiToolCallParser.Parse(u))
                            {
                                if (evt is Agent.Harness.ObservedToolCallDetected d)
                                {
                                    if (!pendingToolCalls.ContainsKey(d.ToolId))
                                        toolCallOrder.Add(d.ToolId);

                                    pendingToolCalls[d.ToolId] = d;
                                    continue;
                                }

                                // For completeness: buffer only tool call intents; pass through anything else.
                                yield return evt;
                            }
                            break;
                        }
                    }
                }
            }
            else if (!string.IsNullOrEmpty(u.Text))
            {
                // Fallback to convenience text property.
                if (reasoningMessageOpen)
                {
                    reasoningMessageOpen = false;
                    yield return new Agent.Harness.ObservedReasoningMessageCompleted(null) { RawUpdate = u };
                }

                assistantMessageOpen = true;
                yield return new Agent.Harness.ObservedAssistantTextDelta(u.Text) { RawUpdate = u };
            }

            // Boundary: if MEAI signals finish, flush buffered tool calls and emit completion observed events.
            if (u.FinishReason is not null)
            {
                foreach (var id in toolCallOrder)
                {
                    if (pendingToolCalls.TryGetValue(id, out var detected))
                        yield return detected;
                }

                toolCallOrder.Clear();
                pendingToolCalls.Clear();

                if (reasoningMessageOpen)
                {
                    reasoningMessageOpen = false;
                    yield return new Agent.Harness.ObservedReasoningMessageCompleted(u.FinishReason.ToString()) { RawUpdate = u };
                }

                assistantMessageOpen = false;
                yield return new Agent.Harness.ObservedAssistantMessageCompleted(u.FinishReason.ToString()) { RawUpdate = u };
            }
        }

        // Hard boundary: stream completed.
        // Flush any buffered tool calls.
        foreach (var id in toolCallOrder)
        {
            if (pendingToolCalls.TryGetValue(id, out var detected))
                yield return detected;
        }

        // Close any open content blocks.
        if (reasoningMessageOpen)
            yield return new Agent.Harness.ObservedReasoningMessageCompleted(null) { RawUpdate = null };

        if (assistantMessageOpen)
            yield return new Agent.Harness.ObservedAssistantMessageCompleted(null) { RawUpdate = null };

        // Always emit a final completion marker so the reducer has a deterministic boundary.
        yield return new Agent.Harness.ObservedAssistantMessageCompleted(null) { RawUpdate = null };
    }
}
