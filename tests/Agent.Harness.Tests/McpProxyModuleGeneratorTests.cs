using System.Text.Json;
using Agent.Acp.Schema;
using FluentAssertions;
using Xunit;

namespace Agent.Harness.Tests;

public sealed class McpProxyModuleGeneratorTests
{
    [Fact]
    public void GenerateServerModules_EmitsFunctions_WithExpectedNames()
    {
        var tools = new[]
        {
            new ToolDefinition(
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
                """).RootElement.Clone()),
            new ToolDefinition(
                Name: "jira__advanced_copy",
                Description: "",
                InputSchema: JsonDocument.Parse("""
                {
                  "type": "object",
                  "additionalProperties": true,
                  "properties": {
                    "from": { "type": "string" },
                    "to": { "type": "string" }
                  },
                  "required": ["from", "to"]
                }
                """).RootElement.Clone()),
        };

        var scripts = Agent.Harness.Shell.Mcp.McpProxyModuleGenerator.Generate(
            tools,
            verbs: Agent.Harness.Shell.Mcp.McpApprovedVerbs.CreateDefault());

        scripts.Should().ContainKey("jira");

        var ps = scripts["jira"];
        ps.Should().Contain("function Get-WorkItems");
        ps.Should().Contain("function Invoke-AdvancedCopy");

        // Required parameters should be mandatory.
        ps.Should().Contain("Mandatory=$true");
        // RawOutput switch should exist.
        ps.Should().Contain("[switch]$RawOutput");
        // RawArgs escape hatch should exist.
        ps.Should().Contain("[hashtable]$RawArgs");
    }

    [Fact]
    public void GenerateServerModules_WhenCmdletNameConflicts_PrefixesServerAfterVerb()
    {
        var tools = new[]
        {
            new ToolDefinition(
                Name: "jira__get_issue",
                Description: "",
                InputSchema: JsonDocument.Parse("{\"type\":\"object\",\"properties\":{},\"additionalProperties\":true}").RootElement.Clone()),
            new ToolDefinition(
                Name: "azure__get_issue",
                Description: "",
                InputSchema: JsonDocument.Parse("{\"type\":\"object\",\"properties\":{},\"additionalProperties\":true}").RootElement.Clone()),
        };

        var scripts = Agent.Harness.Shell.Mcp.McpProxyModuleGenerator.Generate(
            tools,
            verbs: Agent.Harness.Shell.Mcp.McpApprovedVerbs.CreateDefault());

        scripts["jira"].Should().Contain("function Get-JiraIssue");
        scripts["azure"].Should().Contain("function Get-AzureIssue");
    }
}
