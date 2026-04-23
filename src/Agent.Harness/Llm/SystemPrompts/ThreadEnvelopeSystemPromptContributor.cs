using System.Text.Json;
using Agent.Harness.Threads;

namespace Agent.Harness.Llm.SystemPrompts;

public sealed class ThreadEnvelopeSystemPromptContributor : ISystemPromptContributor
{
    public const string FragmentId = "thread_envelope";

    public IEnumerable<SystemPromptFragment> Build(SystemPromptContext ctx)
    {
        var meta = ctx.ThreadMetadata;
        if (meta is null)
            yield break;

        var threadPayload = JsonSerializer.Serialize(new
        {
            threadId = meta.ThreadId,
            parentThreadId = meta.ParentThreadId,
            createdAtIso = meta.CreatedAtIso,
            mode = meta.Mode.ToString().ToLowerInvariant(),
            compactionCount = meta.CompactionCount,
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        yield return new SystemPromptFragment(FragmentId, Order: 2500, $"<thread>{threadPayload}</thread>");
    }
}
