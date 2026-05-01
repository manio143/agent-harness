using System.Collections.Immutable;
using System.Text.Json;
using Agent.Harness.Acp;
using Agent.Harness.Shell;
using FluentAssertions;
using Xunit;

namespace Agent.Harness.Tests;

public sealed class InProcessPowerShellSessionMcpProxyIntegrationTests
{
    [Fact]
    public void Shell_AutoImportsMcpProxyModule_AndInvokesMcpTool()
    {
        var tool = new ToolDefinition(
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

        var invoker = new FakeMcpToolInvoker();

        var wd = Path.Combine(Path.GetTempPath(), "pwsh-mcp-proxy-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(wd);

        using var ps = new InProcessPowerShellSession(
            workingDir: wd,
            mcp: invoker,
            offeredTools: ImmutableArray.Create(tool));

        // Cmdlet should exist and call through to the invoker.
        var r = ps.Execute("$x = Get-WorkItems -Project 'ABC'; $x['ok']", CancellationToken.None);
        r.Success.Should().BeTrue(r.Stderr);
        r.Stdout.Trim().Should().Be("True");

        invoker.LastToolName.Should().Be("jira__get_work_items");
        invoker.LastArgs.Should().ContainKey("project").WhoseValue.Should().Be("ABC");
    }

    private sealed class FakeMcpToolInvoker : IMcpToolInvoker
    {
        public string? LastToolName { get; private set; }
        public IReadOnlyDictionary<string, object?>? LastArgs { get; private set; }

        public bool CanInvoke(string toolName) => toolName == "jira__get_work_items";

        public Task<JsonElement> InvokeAsync(string toolId, string toolName, object args, CancellationToken cancellationToken)
        {
            LastToolName = toolName;
            LastArgs = args as IReadOnlyDictionary<string, object?>;

            var json = JsonDocument.Parse("{\"ok\": true}");
            return Task.FromResult(json.RootElement.Clone());
        }
    }
}
