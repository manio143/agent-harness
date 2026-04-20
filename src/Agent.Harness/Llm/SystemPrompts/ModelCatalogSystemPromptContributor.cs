namespace Agent.Harness.Llm.SystemPrompts;

public sealed class ModelCatalogSystemPromptContributor : ISystemPromptContributor
{
    public const string FragmentId = "model_catalog";

    public IEnumerable<SystemPromptFragment> Build(SystemPromptContext ctx)
    {
        if (string.IsNullOrWhiteSpace(ctx.ModelCatalogPrompt))
            yield break;

        yield return new SystemPromptFragment(FragmentId, Order: 1000, ctx.ModelCatalogPrompt!);
    }
}
