using Agent.Harness.Llm.SystemPrompts;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class SystemPromptComposerTests
{
    private sealed class Contrib(params SystemPromptFragment[] fragments) : ISystemPromptContributor
    {
        public IEnumerable<SystemPromptFragment> Build(SystemPromptContext ctx) => fragments;
    }

    [Fact]
    public void Compose_SortsByOrderThenId_AndIsDeterministic()
    {
        var c = new SystemPromptComposer(new ISystemPromptContributor[]
        {
            new Contrib(new SystemPromptFragment("b", 2000, "B"), new SystemPromptFragment("a", 2000, "A")),
            new Contrib(new SystemPromptFragment("c", 1000, "C")),
        });

        var ctx = new SystemPromptContext("s1", SessionMetadata: null, ModelCatalogPrompt: null);

        var result = c.Compose(ctx);

        result.Select(x => x.Content).Should().Equal("C", "A", "B");
    }

    [Fact]
    public void Compose_ThrowsOnDuplicateIds()
    {
        var c = new SystemPromptComposer(new ISystemPromptContributor[]
        {
            new Contrib(new SystemPromptFragment("x", 1000, "X")),
            new Contrib(new SystemPromptFragment("x", 2000, "X2")),
        });

        var ctx = new SystemPromptContext("s1", SessionMetadata: null, ModelCatalogPrompt: null);

        c.Invoking(x => x.Compose(ctx)).Should().Throw<InvalidOperationException>()
            .WithMessage("*Duplicate system prompt fragment id: x*");
    }
}
