using System;
using System.Collections.Generic;
using Agent.Server;
using FluentAssertions;
using Xunit;

namespace Agent.Server.Tests;

public sealed class ModelCatalogTests
{
    [Fact]
    public void FromOptions_WhenNull_Throws()
    {
        Action act = () => ModelCatalog.FromOptions(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void FromOptions_WhenCatalogEmpty_UsesLegacyOpenAiBlock_AsDefault()
    {
        var opts = new AgentServerOptions
        {
            OpenAI = new AgentServerOptions.OpenAiOptions { BaseUrl = "http://x", ApiKey = "k", Model = "m" },
            Models = new AgentServerOptions.ModelsOptions
            {
                Catalog = new(),
                DefaultModel = "default",
                QuickWorkModel = "default",
            },
        };

        var catalog = ModelCatalog.FromOptions(opts);

        catalog.Models.Should().ContainKey("default");
        catalog.Resolve("default").Model.Should().Be("m");
    }

    [Fact]
    public void FromOptions_WhenDefaultModelUnknown_FallsBackToDefaultFriendlyName()
    {
        var opts = new AgentServerOptions
        {
            OpenAI = new AgentServerOptions.OpenAiOptions { Model = "fallback" },
            Models = new AgentServerOptions.ModelsOptions
            {
                DefaultModel = "nope",
                QuickWorkModel = "nope",
                Catalog = new()
                {
                    ["granite"] = new AgentServerOptions.OpenAiModelOptions { Model = "granite4" },
                },
            },
        };

        var catalog = ModelCatalog.FromOptions(opts);

        catalog.DefaultModel.Should().Be("default");
        catalog.Models.Should().ContainKey("default");
        catalog.Resolve("default").Model.Should().Be("fallback");
    }

    [Fact]
    public void FromOptions_WhenQuickWorkUnknown_FallsBackToDefaultModel()
    {
        var opts = new AgentServerOptions
        {
            Models = new AgentServerOptions.ModelsOptions
            {
                DefaultModel = "granite",
                QuickWorkModel = "does_not_exist",
                Catalog = new()
                {
                    ["granite"] = new AgentServerOptions.OpenAiModelOptions { Model = "granite4" },
                },
            },
        };

        var catalog = ModelCatalog.FromOptions(opts);

        catalog.QuickWorkModel.Should().Be("granite");
    }

    [Fact]
    public void IsKnownModel_Default_IsTrue_AndUnknownIsFalse()
    {
        var catalog = new ModelCatalog(
            Models: new Dictionary<string, AgentServerOptions.OpenAiModelOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["granite"] = new AgentServerOptions.OpenAiModelOptions { Model = "g" },
            },
            DefaultModel: "granite",
            QuickWorkModel: "granite");

        catalog.IsKnownModel("default").Should().BeTrue();
        catalog.IsKnownModel("granite").Should().BeTrue();
        catalog.IsKnownModel("unknown").Should().BeFalse();
        catalog.IsKnownModel(" ").Should().BeFalse();
    }

    [Fact]
    public void Resolve_WhenUnknown_FallsBackToDefaultModel()
    {
        var catalog = new ModelCatalog(
            Models: new Dictionary<string, AgentServerOptions.OpenAiModelOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["granite"] = new AgentServerOptions.OpenAiModelOptions { Model = "g" },
            },
            DefaultModel: "granite",
            QuickWorkModel: "granite");

        catalog.Resolve("does_not_exist").Model.Should().Be("g");
    }

    [Fact]
    public void Resolve_DefaultKeyword_IsCaseInsensitive()
    {
        var catalog = new ModelCatalog(
            Models: new Dictionary<string, AgentServerOptions.OpenAiModelOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["g"] = new AgentServerOptions.OpenAiModelOptions { Model = "g" },
            },
            DefaultModel: "g",
            QuickWorkModel: "g");

        catalog.Resolve("DEFAULT").Model.Should().Be("g");
    }
}
