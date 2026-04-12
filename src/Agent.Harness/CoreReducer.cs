using System.Collections.Immutable;

namespace Agent.Harness;

public sealed record CoreOptions(bool EmitModelInvokedEvents = false);

/// <summary>
/// Functional core reducer.
///
/// This type is intentionally minimal right now. We will evolve behavior TDD-style.
/// </summary>
public static class Core
{
    public static ReduceResult Reduce(SessionState state, ObservedChatEvent evt, CoreOptions? options = null)
    {
        // TDD: implement in later steps.
        throw new NotImplementedException();
    }

    public static ImmutableArray<ChatMessage> RenderPrompt(SessionState state)
    {
        // TDD: implement in later steps.
        throw new NotImplementedException();
    }
}
