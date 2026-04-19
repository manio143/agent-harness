using Agent.Acp.Acp;
using Agent.Acp.Schema;
using Agent.Server;
using FluentAssertions;
using Xunit;

namespace Agent.Server.Tests;

public sealed class McpDiscoveryParseTests
{
    [Fact]
    public void ParseStdioServers_WhenServerHasHttpTransport_ThrowsInvalidParams()
    {
        var s = new McpServer();
        s.AdditionalProperties["http"] = new { url = "http://example" };

        var req = new NewSessionRequest { Cwd = "/repo", McpServers = new List<McpServer> { s } };

        var act = () => McpDiscovery.ParseStdioServers(req);

        act.Should().Throw<AcpJsonRpcException>()
            .Which.Code.Should().Be(-32602);
    }

    [Fact]
    public void ParseStdioServers_WhenNoStdio_SkipsServer()
    {
        var req = new NewSessionRequest { Cwd = "/repo", McpServers = new List<McpServer> { new McpServer() } };

        McpDiscovery.ParseStdioServers(req).Should().BeEmpty();
    }

    [Fact]
    public void ParseStdioServers_WhenMissingCommand_ThrowsInvalidParams()
    {
        var s = new McpServer();
        s.AdditionalProperties["stdio"] = new { name = "srv" };

        var req = new NewSessionRequest { Cwd = "/repo", McpServers = new List<McpServer> { s } };

        var act = () => McpDiscovery.ParseStdioServers(req);

        act.Should().Throw<AcpJsonRpcException>()
            .Which.Message.Should().Contain("missing command");
    }

    [Fact]
    public void ParseStdioServers_ParsesNameArgsEnv_AndNormalizesServerId()
    {
        var s = new McpServer();
        s.AdditionalProperties["stdio"] = new
        {
            name = "My Server",
            command = "node",
            args = new object?[] { "-v", null, 12, " " },
            env = new[]
            {
                new { key = "A", value = "1" },
                new { key = "B", value = "2" },
            },
        };

        var req = new NewSessionRequest { Cwd = "/repo", McpServers = new List<McpServer> { s } };

        var parsed = McpDiscovery.ParseStdioServers(req);
        parsed.Should().HaveCount(1);

        parsed[0].ServerId.Should().Be("my_server");
        parsed[0].Command.Should().Be("node");
        parsed[0].Arguments.Should().BeEquivalentTo(new[] { "-v" });
        parsed[0].Env.Should().ContainKey("A").WhoseValue.Should().Be("1");
        parsed[0].Env.Should().ContainKey("B").WhoseValue.Should().Be("2");
        parsed[0].WorkingDirectory.Should().Be("/repo");
    }

    [Fact]
    public void ParseStdioServers_WhenNormalizedNameEmpty_UsesStableFallbackId()
    {
        var s = new McpServer();
        s.AdditionalProperties["stdio"] = new
        {
            name = "___",
            command = "node",
        };

        var req = new NewSessionRequest { Cwd = "/repo", McpServers = new List<McpServer> { s } };

        var parsed = McpDiscovery.ParseStdioServers(req);
        parsed.Should().ContainSingle();
        parsed[0].ServerId.Should().Be("mcp_1");
    }
}
