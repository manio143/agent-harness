using Agent.Server;
using FluentAssertions;
using Xunit;

namespace Agent.Server.Tests;

public sealed class ModelCatalogResolutionTests
{
    [Fact]
    public void Resolve_WhenCatalogEmpty_SynthesizesDefaultFromLegacyOpenAiBlock()
    {
        var opts = new AgentServerOptions
        {
            OpenAI = new AgentServerOptions.OpenAiOptions
            {
                BaseUrl = "http://x",
                ApiKey = "k",
                Model = "m",
            }
        };

        var catalog = ModelCatalog.FromOptions(opts);

        catalog.DefaultModel.Should().Be("default");
        catalog.Models.Should().ContainKey("default");
        catalog.Models["default"].Model.Should().Be("m");
    }

    [Fact]
    public void Resolve_WhenDefaultModelMissing_FallsBackToDefault()
    {
        var opts = new AgentServerOptions
        {
            Models = new AgentServerOptions.ModelsOptions
            {
                DefaultModel = "nope",
                Catalog = new()
                {
                    ["default"] = new AgentServerOptions.OpenAiModelOptions { BaseUrl = "http://x", ApiKey = "k", Model = "m" },
                    ["granite"] = new AgentServerOptions.OpenAiModelOptions { BaseUrl = "http://y", ApiKey = "k2", Model = "granite4:3b" },
                }
            }
        };

        var catalog = ModelCatalog.FromOptions(opts);

        catalog.DefaultModel.Should().Be("default");
    }
}
