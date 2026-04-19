using System.Collections.Generic;
using Agent.Server;
using FluentAssertions;
using Xunit;

namespace Agent.Server.Tests;

public sealed class OpenAiChatClientFactoryTests
{
    [Fact]
    public void Get_WhenUnknownModel_FallsBackToDefaultModelNameCacheSlot()
    {
        var opts = new AgentServerOptions
        {
            Models = new AgentServerOptions.ModelsOptions
            {
                DefaultModel = "default",
                Catalog = new()
                {
                    ["default"] = new AgentServerOptions.OpenAiModelOptions { BaseUrl = "http://x", ApiKey = "k", Model = "m" },
                },
            }
        };

        var catalog = ModelCatalog.FromOptions(opts);
        var factory = new OpenAiChatClientFactory(catalog);

        // We can't easily inspect OpenAI client internals; this test just asserts we don't throw and caching works.
        var c1 = factory.Get("does_not_exist");
        var c2 = factory.Get("default");

        c1.Should().BeSameAs(c2);
    }
}
