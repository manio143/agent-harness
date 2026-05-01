using System.Collections.Immutable;
using System.Text.Json;
using Agent.Harness.Acp;
using Agent.Harness.Shell;
using FluentAssertions;
using Xunit;

namespace Agent.Harness.Tests;

public sealed class InProcessPowerShellSessionMcpProxySchemaRefreshIntegrationTests
{
    [Fact]
    public void Shell_RefreshesProxyModules_WhenToolSchemaChanges()
    {
        var toolV1 = new ToolDefinition(
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

        var toolV2 = new ToolDefinition(
            Name: "jira__get_issue",
            Description: "",
            InputSchema: JsonDocument.Parse("""
            {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "id": { "type": "string" },
                "mode": { "type": "string", "enum": ["fast", "slow"], "description": "Mode" }
              },
              "required": ["id", "mode"]
            }
            """).RootElement.Clone());

        var invoker = new FakeMcpToolInvoker();

        var wd = Path.Combine(Path.GetTempPath(), "pwsh-mcp-proxy-schema-refresh-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(wd);

        using var ps = new InProcessPowerShellSession(
            workingDir: wd,
            mcp: invoker,
            offeredTools: ImmutableArray.Create(toolV1));

        // No ValidateSet in v1.
        var before = ps.Execute("(Get-Command Get-Issue).Definition", CancellationToken.None);
        before.Success.Should().BeTrue(before.Stderr);
        before.Stdout.Should().NotContain("ValidateSet('fast','slow')");

        // Update schema.
        ps.UpdateOfferedTools(ImmutableArray.Create(toolV2));

        // ValidateSet should appear after refresh.
        var after = ps.Execute("(Get-Command Get-Issue).Definition", CancellationToken.None);
        after.Success.Should().BeTrue(after.Stderr);
        after.Stdout.Should().Contain("ValidateSet('fast','slow')");

        // Comment-based help doesn't appear in .Definition; validate via Get-Help.
        var help = ps.Execute("(Get-Help Get-Issue -Parameter Mode).Description.Text | Out-String", CancellationToken.None);
        help.Success.Should().BeTrue(help.Stderr);
        help.Stdout.Should().Contain("Mode");
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
