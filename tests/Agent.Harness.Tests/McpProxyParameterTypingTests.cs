using System.Text.Json;
using Agent.Acp.Schema;
using FluentAssertions;
using Xunit;

namespace Agent.Harness.Tests;

public sealed class McpProxyParameterTypingTests
{
    [Fact]
    public void OptionalParams_AreStrictlyTyped_WhenRepresentable()
    {
        var tool = new ToolDefinition(
            Name: "x__get_demo",
            Description: "",
            InputSchema: JsonDocument.Parse("""
            {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "count": { "type": "integer" },
                "ratio": { "type": "number" },
                "enabled": { "type": "boolean" },
                "names": { "type": "array", "items": { "type": "string" } }
              },
              "required": []
            }
            """).RootElement.Clone());

        var scripts = Agent.Harness.Shell.Mcp.McpProxyModuleGenerator.Generate(
            new[] { tool },
            verbs: Agent.Harness.Shell.Mcp.McpApprovedVerbs.CreateDefault());

        var ps = scripts["x"];
        ps.Should().Contain("[int]$Count");
        ps.Should().Contain("[double]$Ratio");
        ps.Should().Contain("[bool]$Enabled");
        ps.Should().Contain("[string[]]$Names");
    }

    [Fact]
    public void ParameterHelp_UsesSchemaDescriptions()
    {
        var tool = new ToolDefinition(
            Name: "x__get_demo",
            Description: "",
            InputSchema: JsonDocument.Parse("""
            {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "project": { "type": "string", "description": "Project key" }
              },
              "required": ["project"]
            }
            """).RootElement.Clone());

        var scripts = Agent.Harness.Shell.Mcp.McpProxyModuleGenerator.Generate(
            new[] { tool },
            verbs: Agent.Harness.Shell.Mcp.McpApprovedVerbs.CreateDefault());

        scripts["x"].Should().Contain(".PARAMETER Project");
        scripts["x"].Should().Contain("Project key");
    }
}
