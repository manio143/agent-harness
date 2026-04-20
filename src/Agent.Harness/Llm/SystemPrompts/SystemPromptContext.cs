using Agent.Harness.Persistence;

namespace Agent.Harness.Llm.SystemPrompts;

public sealed record SystemPromptContext(
    string SessionId,
    SessionMetadata? SessionMetadata,
    string? ModelCatalogPrompt);
