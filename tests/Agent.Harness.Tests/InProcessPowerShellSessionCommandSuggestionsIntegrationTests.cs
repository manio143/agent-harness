using System.Collections.Immutable;
using System.Threading;
using Agent.Harness.Llm.CommandSuggestions;
using Agent.Harness.Shell;
using FluentAssertions;
using Xunit;

namespace Agent.Harness.Tests;

public sealed class InProcessPowerShellSessionCommandSuggestionsIntegrationTests
{
    [Fact]
    public void Shell_Defines_FindAgentCommand_And_ItIsCallable_WhenDisabled()
    {
        var wd = Path.Combine(Path.GetTempPath(), "pwsh-cmd-suggest-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(wd);

        using var ps = new InProcessPowerShellSession(
            workingDir: wd,
            commandIntentSuggester: NullCommandIntentSuggester.Instance,
            includeSuggestionsInShell: false,
            offeredTools: ImmutableArray<ToolDefinition>.Empty);

        var exists = ps.Execute("Get-Command -Name Find-AgentCommand -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name", CancellationToken.None);
        exists.Success.Should().BeTrue(exists.Stderr);
        exists.Stdout.Trim().Should().Be("Find-AgentCommand");

        var call = ps.Execute("ConvertTo-Json -Compress -InputObject @(Find-AgentCommand -Intent 'list files')", CancellationToken.None);
        call.Success.Should().BeTrue(call.Stderr);
        call.Stdout.Trim().Should().Be("[]");
    }
}
