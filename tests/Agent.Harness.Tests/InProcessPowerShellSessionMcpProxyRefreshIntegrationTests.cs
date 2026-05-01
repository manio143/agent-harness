using System.Collections.Immutable;
using System.Text.Json;
using Agent.Harness.Acp;
using Agent.Harness.Shell;
using FluentAssertions;
using Xunit;

namespace Agent.Harness.Tests;

public sealed class InProcessPowerShellSessionMcpProxyRefreshIntegrationTests
{
    [Fact]
    public void Shell_RefreshesProxyModules_WhenOfferedToolsChange()
    {
        var tool1 = new ToolDefinition(
            Name: "jira__get_work_items",
            Description: "",
            InputSchema: JsonDocument.Parse("""
            {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "project": { "type": "string" }
              },
              "required": ["project"]
            }
            """).RootElement.Clone());

        var tool2 = new ToolDefinition(
            Name: "jira__get_issue",
            Description: "",
            InputSchema: JsonDocument.Parse("""
            {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "id": { "type": "string" }
              },
              "required": ["id"]
            }
            """).RootElement.Clone());

        var invoker = new FakeMcpToolInvoker();

        var wd = Path.Combine(Path.GetTempPath(), "pwsh-mcp-proxy-refresh-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(wd);

        using var ps = new InProcessPowerShellSession(
            workingDir: wd,
            mcp: invoker,
            offeredTools: ImmutableArray.Create(tool1));

        ps.UpdateOfferedTools(ImmutableArray.Create(tool1, tool2));

        var after = ps.Execute("(Get-Command -Name Get-Issue -ErrorAction SilentlyContinue) -ne $null", CancellationToken.None);
        after.Success.Should().BeTrue(after.Stderr);
        after.Stdout.Trim().Should().Be("True");
    }

    private sealed class FakeMcpToolInvoker : IMcpToolInvoker
    {
        public bool CanInvoke(string toolName) => toolName.StartsWith("jira__", StringComparison.Ordinal);

        public Task<JsonElement> InvokeAsync(string toolId, string toolName, object args, CancellationToken cancellationToken)
        {
            var json = JsonDocument.Parse("{\"ok\": true}");
            return Task.FromResult(json.RootElement.Clone());
        }
    }
}
