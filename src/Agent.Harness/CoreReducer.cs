using System.Collections.Immutable;

namespace Agent.Harness;

public sealed record CoreOptions(
    bool EmitModelInvokedEvents = false,
    bool CommitAssistantTextDeltas = false,
    bool CommitReasoningTextDeltas = false);

/// <summary>
/// Functional core reducer.
///
/// Rules (initial slice):
/// - ObservedUserMessage commits UserMessage.
/// - ObservedAssistantTextDelta appends to the in-flight assistant buffer.
/// - ObservedAssistantMessageCompleted flushes the assistant buffer into AssistantMessage.
/// - RenderPrompt uses ONLY committed user/assistant messages (debug events are ignored).
/// </summary>
public static class Core
{
    public static ReduceResult Reduce(SessionState state, ObservedChatEvent evt, CoreOptions? options = null)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        if (evt is null) throw new ArgumentNullException(nameof(evt));

        switch (evt)
        {
            case ObservedUserMessage m:
                return Commit(state, new UserMessage(m.Text));

            case ObservedAssistantTextDelta d:
            {
                var next = state with
                {
                    Buffer = state.Buffer with
                    {
                        AssistantMessageOpen = true,
                        AssistantText = state.Buffer.AssistantText + d.Text,
                    },
                };

                if (options?.CommitAssistantTextDeltas == true)
                {
                    var delta = new AssistantTextDelta(d.Text);
                    var committed = next.Committed.Add(delta);
                    return new ReduceResult(next with { Committed = committed }, ImmutableArray.Create<SessionEvent>(delta));
                }

                return new ReduceResult(next, ImmutableArray<SessionEvent>.Empty);
            }

            case ObservedReasoningTextDelta d:
            {
                if (options?.CommitReasoningTextDeltas == true)
                {
                    var delta = new ReasoningTextDelta(d.Text);
                    var committed = state.Committed.Add(delta);
                    return new ReduceResult(state with { Committed = committed }, ImmutableArray.Create<SessionEvent>(delta));
                }

                return new ReduceResult(state, ImmutableArray<SessionEvent>.Empty);
            }

            case ObservedAssistantMessageCompleted:
                return FlushAssistant(state);

            default:
                return new ReduceResult(state, ImmutableArray<SessionEvent>.Empty);
        }
    }

    public static ImmutableArray<ChatMessage> RenderPrompt(SessionState state)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));

        var builder = ImmutableArray.CreateBuilder<ChatMessage>();

        foreach (var evt in state.Committed)
        {
            switch (evt)
            {
                case UserMessage u:
                    builder.Add(new ChatMessage(ChatRole.User, u.Text));
                    break;
                case AssistantMessage a:
                    builder.Add(new ChatMessage(ChatRole.Assistant, a.Text));
                    break;
                default:
                    // Ignore debug-only and non-chat committed events for prompt rendering.
                    break;
            }
        }

        return builder.ToImmutable();
    }

    private static ReduceResult Commit(SessionState state, SessionEvent evt)
    {
        var committed = state.Committed.Add(evt);
        var next = state with { Committed = committed };
        return new ReduceResult(next, ImmutableArray.Create(evt));
    }

    private static ReduceResult FlushAssistant(SessionState state)
    {
        if (!state.Buffer.AssistantMessageOpen && string.IsNullOrEmpty(state.Buffer.AssistantText))
        {
            // Nothing to flush.
            return new ReduceResult(state, ImmutableArray<SessionEvent>.Empty);
        }

        var text = state.Buffer.AssistantText;
        var nextState = state with { Buffer = TurnBuffer.Empty };

        // Commit even if text is empty: treat boundary as a message completion.
        // (We can tighten this later if desired.)
        return Commit(nextState, new AssistantMessage(text));
    }
}
