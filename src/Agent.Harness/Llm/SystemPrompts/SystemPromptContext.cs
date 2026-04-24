using System.Collections.Immutable;
using Agent.Harness.Persistence;

namespace Agent.Harness.Llm.SystemPrompts;

using Agent.Harness.Threads;

public sealed record SystemPromptContext(
    string SessionId,
    SessionMetadata? SessionMetadata,
    string? ModelCatalogPrompt,
    string ThreadId,
    ThreadMetadata? ThreadMetadata,
    ImmutableHashSet<string>? OfferedToolNames = null);
