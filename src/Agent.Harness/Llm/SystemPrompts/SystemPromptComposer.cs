namespace Agent.Harness.Llm.SystemPrompts;

public sealed class SystemPromptComposer
{
    private readonly IReadOnlyList<ISystemPromptContributor> _contributors;

    public SystemPromptComposer(IEnumerable<ISystemPromptContributor> contributors)
    {
        _contributors = contributors?.ToList() ?? throw new ArgumentNullException(nameof(contributors));
    }

    public IReadOnlyList<SystemPromptFragment> Compose(SystemPromptContext ctx)
    {
        var fragments = _contributors
            .SelectMany(c => c.Build(ctx) ?? Array.Empty<SystemPromptFragment>())
            .Where(f => !string.IsNullOrWhiteSpace(f.Content))
            .OrderBy(f => f.Order)
            .ThenBy(f => f.Id, StringComparer.Ordinal)
            .ToList();

        // Guardrail: Ids must be unique to avoid accidental duplicate prefix tokens.
        var dup = fragments
            .GroupBy(f => f.Id, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);

        if (dup is not null)
            throw new InvalidOperationException($"Duplicate system prompt fragment id: {dup.Key}");

        return fragments;
    }

    public string ComposeText(SystemPromptContext ctx)
        => string.Join("\n\n", Compose(ctx).Select(f => f.Content));
}
