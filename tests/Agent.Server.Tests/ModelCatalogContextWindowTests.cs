using Agent.Server;
using FluentAssertions;
using Xunit;

namespace Agent.Server.Tests;

public sealed class ModelCatalogContextWindowTests
{
    [Fact]
    public void TryGetContextWindowTokensByProviderModel_WhenKnown_ReturnsTokens()
    {
        var catalog = new ModelCatalog(
            Models: new Dictionary<string, AgentServerOptions.OpenAiModelOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["groq"] = new AgentServerOptions.OpenAiModelOptions { Model = "qwen/qwen3-32b", ContextWindowK = 32 },
            },
            DefaultModel: "groq",
            QuickWorkModel: "groq");

        catalog.TryGetContextWindowTokensByProviderModel("qwen/qwen3-32b").Should().Be(32_000);
    }

    [Fact]
    public void TryGetContextWindowTokensByProviderModel_WhenUnknown_ReturnsNull()
    {
        var catalog = new ModelCatalog(
            Models: new Dictionary<string, AgentServerOptions.OpenAiModelOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["groq"] = new AgentServerOptions.OpenAiModelOptions { Model = "qwen/qwen3-32b", ContextWindowK = 32 },
            },
            DefaultModel: "groq",
            QuickWorkModel: "groq");

        catalog.TryGetContextWindowTokensByProviderModel("unknown").Should().BeNull();
    }

    [Fact]
    public void TryGetContextWindowTokensByProviderModel_WhenContextWindowMissing_ReturnsNull()
    {
        var catalog = new ModelCatalog(
            Models: new Dictionary<string, AgentServerOptions.OpenAiModelOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["groq"] = new AgentServerOptions.OpenAiModelOptions { Model = "qwen/qwen3-32b" },
            },
            DefaultModel: "groq",
            QuickWorkModel: "groq");

        catalog.TryGetContextWindowTokensByProviderModel("qwen/qwen3-32b").Should().BeNull();
    }
}
