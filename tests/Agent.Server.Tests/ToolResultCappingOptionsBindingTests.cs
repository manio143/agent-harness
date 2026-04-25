using Agent.Server;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace Agent.Server.Tests;

public sealed class ToolResultCappingOptionsBindingTests
{
    [Fact]
    public void AgentServerOptions_binds_ToolResultCapping_from_configuration()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AgentServer:ToolResultCapping:Enabled"] = "true",
                ["AgentServer:ToolResultCapping:MaxStringChars"] = "123",
                ["AgentServer:ToolResultCapping:MaxArrayItems"] = "9",
                ["AgentServer:ToolResultCapping:MaxObjectProperties"] = "7",
                ["AgentServer:ToolResultCapping:MaxDepth"] = "5",
            })
            .Build();

        var opts = new AgentServerOptions();
        cfg.GetSection("AgentServer").Bind(opts);

        opts.ToolResultCapping.Enabled.Should().BeTrue();
        opts.ToolResultCapping.MaxStringChars.Should().Be(123);
        opts.ToolResultCapping.MaxArrayItems.Should().Be(9);
        opts.ToolResultCapping.MaxObjectProperties.Should().Be(7);
        opts.ToolResultCapping.MaxDepth.Should().Be(5);
    }
}
