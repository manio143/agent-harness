using System.Text.Json;

namespace Agent.Harness.Llm.SystemPrompts;

public sealed class SessionEnvelopeSystemPromptContributor : ISystemPromptContributor
{
    public const string FragmentId = "session_envelope";

    public IEnumerable<SystemPromptFragment> Build(SystemPromptContext ctx)
    {
        var meta = ctx.SessionMetadata;
        var sessionPayload = JsonSerializer.Serialize(new
        {
            sessionId = ctx.SessionId,
            cwd = meta?.Cwd,
            createdAtIso = meta?.CreatedAtIso,
            updatedAtIso = meta?.UpdatedAtIso,
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        yield return new SystemPromptFragment(FragmentId, Order: 2000, $"<session>{sessionPayload}</session>");
    }
}
