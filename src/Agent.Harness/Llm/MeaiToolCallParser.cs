using Agent.Harness;
using Microsoft.Extensions.AI;
using System.Collections.Generic;

namespace Agent.Harness.Llm;

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
                // TODO(tool-calls): Handle multi-chunk / incremental FunctionCallContent updates.
                // Some providers may stream tool calls in multiple partial chunks (e.g. args arriving over time),
                // or repeat the same call id with updated arguments. We currently treat each FunctionCallContent
                // as a complete tool intent.

                // ToolId: provider doesn't always give one; use a deterministic placeholder.
                // The harness will replace/assign stable ids at the boundary if needed.
                var toolId = call.CallId ?? Guid.NewGuid().ToString("N");

                yield return new ObservedToolCallDetected(
                    ToolId: toolId,
                    ToolName: call.Name,
                    Args: call.Arguments ?? new Dictionary<string, object?>())
                {
                    RawUpdate = update,
                };
            }
        }
    }
}
