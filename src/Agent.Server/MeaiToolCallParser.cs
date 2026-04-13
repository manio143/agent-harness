using Agent.Harness;
using Microsoft.Extensions.AI;

namespace Agent.Server;

public static class MeaiToolCallParser
{
    public static IEnumerable<ObservedChatEvent> Parse(ChatResponseUpdate update)
    {
        if (update is null) throw new ArgumentNullException(nameof(update));

        if (update.Contents is null)
            yield break;

        foreach (var c in update.Contents)
        {
            // Mode A: model proposes tool-call intent; we emit an observation.
            if (c is FunctionCallContent call)
            {
                // ToolId: provider doesn't always give one; use a deterministic placeholder.
                // The harness will replace/assign stable ids at the boundary if needed.
                var toolId = call.Id ?? Guid.NewGuid().ToString("N");

                yield return new ObservedToolCallDetected(
                    ToolId: toolId,
                    ToolName: call.Name,
                    Args: call.Arguments);
            }
        }
    }
}
