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
                // NOTE: Some providers stream tool calls in multiple partial chunks (cumulative/incremental deltas).
                // The parser stays intentionally dumb/lossless: each FunctionCallContent becomes a tool intent snapshot.
                // Incremental merging/deduping happens in MeaiObservedEventSource (buffers latest per ToolId and flushes once).

                // ToolId: provider doesn't always give one.
                // If CallId is absent, use a deterministic id so cumulative deltas can still be deduped.
                var toolId = string.IsNullOrWhiteSpace(call.CallId)
                    ? CreateDeterministicToolId(call.Name, call.Arguments)
                    : call.CallId;

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
