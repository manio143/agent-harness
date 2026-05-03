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
        "Command suggestions (intent → cmdlet names):\n" +
        "- The shell provides Find-AgentCommand -Intent \"...\" to get suggested commands for a natural-language intent.\n" +
        "- Suggestions are best-effort and may be disabled by configuration (in that case, the command returns an empty list).\n" +
        "\n" +
        "Project filesystem drive (project:):\n" +
        "- If available in the shell, a PSDrive named project: maps directly to the session cwd on the agent host.\n" +
        "- project: supports normal filesystem navigation and content commands, including Get-ChildItem, Get-Content, and Set-Content.\n" +
        "- project: is rooted at the session cwd.\n" +
        "  Examples: project:\\README.md or project:/src/Agent.Server\n" +
        "- Use sandbox: for temporary agent-local artifacts and project: for files that belong to the current project.\n";

    public IEnumerable<SystemPromptFragment> Build(SystemPromptContext ctx)
    {
        // Capability gating: only include shell guidance when the shell tool is offered.
        if (ctx.OfferedToolNames is not null && !ctx.OfferedToolNames.Contains("agent_shell_execute"))
            yield break;

        yield return new SystemPromptFragment(FragmentId, Order: 2650, $"<shell>{Text}</shell>");
    }
}
