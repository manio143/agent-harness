namespace Agent.Harness.Llm.SystemPrompts;

public sealed class ThreadCapabilitiesSystemPromptContributor : ISystemPromptContributor
{
    public const string FragmentId = "thread_capabilities";

    private const string Text =
        "Thread capabilities:\n" +
        "- Tool availability can differ per thread (capabilities may restrict the tool surface).\n" +
        "- Only call tools that appear in the current tool catalog. If a tool is not listed, you MUST NOT call it (it will fail).\n" +
        "- If threading tools (thread_*) are not present, you cannot manage threads from this thread.";

    public IEnumerable<SystemPromptFragment> Build(SystemPromptContext ctx)
    {
        // Only include this guidance when the current tool surface actually includes threading tools.
        // If thread_* tools are not offered, the note would be redundant and just adds prompt weight.
        var offered = ctx.OfferedToolNames;
        if (offered is null || !offered.Any(n => n.StartsWith("thread_", StringComparison.Ordinal)))
            yield break;

        yield return new SystemPromptFragment(FragmentId, Order: 2550, $"<capabilities>{Text}</capabilities>");
    }
}
