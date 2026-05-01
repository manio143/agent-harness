using FluentAssertions;
using Xunit;

namespace Agent.Harness.Tests;

public sealed class McpProxyCmdletNameMapperTests
{
    [Theory]
    [InlineData("get_work_items", "Get-WorkItems")]
    [InlineData("work_items_get", "Get-WorkItems")]
    [InlineData("advanced_copy", "Invoke-AdvancedCopy")]
    public void MapToolName_UsesCuratedVerbList_WithFirstOrLastSegment(string toolName, string expected)
    {
        var verbs = Agent.Harness.Shell.Mcp.McpApprovedVerbs.CreateDefault();
        var mapped = Agent.Harness.Shell.Mcp.McpCmdletNameMapper.Map(toolName, verbs);
        mapped.CmdletName.Should().Be(expected);
    }

    [Fact]
    public void MapToolName_WhenFirstAndLastAreVerbs_PrefersFirst()
    {
        var verbs = Agent.Harness.Shell.Mcp.McpApprovedVerbs.CreateDefault();
        var mapped = Agent.Harness.Shell.Mcp.McpCmdletNameMapper.Map("get_item_get", verbs);
        mapped.CmdletName.Should().Be("Get-ItemGet");
    }
}
