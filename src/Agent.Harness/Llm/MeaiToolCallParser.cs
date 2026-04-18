using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agent.Harness;
using Microsoft.Extensions.AI;

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

                // ToolId: provider doesn't always give one.
                // If CallId is absent, use a deterministic id so cumulative deltas can still be deduped.
                var toolId = call.CallId ?? CreateDeterministicToolId(call.Name, call.Arguments);

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

    private static string CreateDeterministicToolId(string toolName, IDictionary<string, object?>? args)
    {
        // Stable over the lifetime of the process. Purpose is dedupe of cumulative deltas when provider omits call id.
        // This is not a security boundary.
        var payload = JsonSerializer.Serialize(new { toolName, args }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return "auto_" + Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
