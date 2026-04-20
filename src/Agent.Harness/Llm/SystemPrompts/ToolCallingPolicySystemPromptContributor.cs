namespace Agent.Harness.Llm.SystemPrompts;

public sealed class ToolCallingPolicySystemPromptContributor : ISystemPromptContributor
{
    public const string FragmentId = "tool_calling_policy";

    private const string Prompt =
        "Tool calling policy:\n" +
        "- You MUST call `report_intent` before calling any other tool in a turn.\n" +
        "- After a new user message arrives, you MUST call `report_intent` again before calling other tools.\n" +
        "- `report_intent` only reports intent; it does not perform work.";

    public IEnumerable<SystemPromptFragment> Build(SystemPromptContext ctx)
    {
        yield return new SystemPromptFragment(FragmentId, Order: 1500, Prompt);
    }
}
