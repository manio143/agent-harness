namespace Agent.Harness.Llm.SystemPrompts;

public sealed class ThreadingGuidanceSystemPromptContributor : ISystemPromptContributor
{
    public const string FragmentId = "threading_guidance";

    private const string Text =
        "Threading guidance:\n" +
        "- Use child threads when you have a well-defined sub-task with clear instructions.\n" +
        "- Prefer keeping large outputs (e.g. long documents, file contents) in child threads to protect the main thread context window.\n" +
        "\n" +
        "Thread modes (MUST specify when starting a thread):\n" +
        "- mode=single: one-shot task thread. The thread is closed automatically once it becomes idle and its inbox is empty. It cannot receive more messages and disappears from thread_list.\n" +
        "  Examples: writing a doc, generating a report, summarizing a folder scan, producing a large artifact.\n" +
        "- mode=multi: long-lived helper thread. It can accept multiple tasks/messages over time, even after becoming idle.\n" +
        "  Examples: parallel ongoing work streams where the child accumulates its own context and handles follow-up tasks.\n" +
        "\n" +
        "Stopping threads:\n" +
        "- If a thread was started with an incorrect task or needs correction, call thread_stop on that thread and then start a new thread with the corrected task.";

    public IEnumerable<SystemPromptFragment> Build(SystemPromptContext ctx)
    {
        yield return new SystemPromptFragment(FragmentId, Order: 2600, $"<threading>{Text}</threading>");
    }
}
