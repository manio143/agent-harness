using System.Collections.Immutable;

namespace Agent.Harness.Tools.Handlers;

public interface IToolHandler
{
    ToolDefinition Definition { get; }

    Task<ImmutableArray<ObservedChatEvent>> ExecuteAsync(SessionState state, ExecuteToolCall tool, CancellationToken cancellationToken);
}
