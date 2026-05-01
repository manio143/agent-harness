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
        ps.Should().Contain("MCP tool proxy for jira__get_work_items");
        ps.Should().Contain("function Get-WorkItems");
        ps.Should().Contain("function Invoke-AdvancedCopy");

        // Required parameters should be mandatory.
        ps.Should().Contain("Mandatory=$true");
        // RawOutput switch should exist.
        ps.Should().Contain("[switch]$RawOutput");
        // RawArgs escape hatch should exist.
        ps.Should().Contain("[hashtable]$RawArgs");

        // ValidateSet for enum strings (phase 2).
        var enumTool = new ToolDefinition(
            Name: "jira__get_mode",
            Description: "",
            InputSchema: JsonDocument.Parse("""
            {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "mode": { "type": "string", "enum": ["fast", "slow"] }
              },
              "required": ["mode"]
            }
            """).RootElement.Clone());

        var withEnum = Agent.Harness.Shell.Mcp.McpProxyModuleGenerator.Generate(
            tools.Append(enumTool),
            verbs: Agent.Harness.Shell.Mcp.McpApprovedVerbs.CreateDefault());

        withEnum["jira"].Should().Contain("ValidateSet('fast','slow')");
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
