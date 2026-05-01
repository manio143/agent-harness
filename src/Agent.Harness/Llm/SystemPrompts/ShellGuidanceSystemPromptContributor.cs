namespace Agent.Harness.Llm.SystemPrompts;

public sealed class ShellGuidanceSystemPromptContributor : ISystemPromptContributor
{
    public const string FragmentId = "shell_guidance";

    private const string Text =
        "PowerShell shell tool (agent_shell_execute):\n" +
        "- If the tool agent_shell_execute is present in the tool catalog, you may use it to execute PowerShell Core scripts.\n" +
        "- The PowerShell session state (variables/functions) is scoped per thread and persists across calls.\n" +
        "- The agent-local working directory is exposed as a PSDrive named sandbox:. Use relative paths or sandbox:\\... to write temporary artifacts.\n" +
        "\n" +
        "ACP client filesystem drive (client:):\n" +
        "- If available in the shell, a PSDrive named client: maps file reads/writes to ACP fs/read_text_file and fs/write_text_file (remote client filesystem).\n" +
        "- client: is rooted at the ACP session cwd. Paths must be relative to that root.\n" +
        "  Examples: client:\\demo.txt or client:/dir/file.txt\n" +
        "  Disallowed: client:\\C:\\x.txt or client:\\env:PATH (embedded drive/provider)\n" +
        "- Listing is not supported (ACP has no directory listing APIs). Prefer direct Get-Content/Set-Content on known paths.\n" +
        "- If ACP fs capabilities are missing, client: operations will error.\n";

    public IEnumerable<SystemPromptFragment> Build(SystemPromptContext ctx)
    {
        // Capability gating: only include shell guidance when the shell tool is offered.
        if (ctx.OfferedToolNames is not null && !ctx.OfferedToolNames.Contains("agent_shell_execute"))
            yield break;

        yield return new SystemPromptFragment(FragmentId, Order: 2650, $"<shell>{Text}</shell>");
    }
}
