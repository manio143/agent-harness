using System.Collections.Immutable;
using Agent.Harness.Tools.Handlers;

namespace Agent.Harness.Tools.Executors;

public sealed class RegistryToolCallExecutor(ToolRegistry registry) : IToolCallExecutor
{
    public bool CanExecute(string toolName) => registry.CanExecute(toolName);

    public Task<ImmutableArray<ObservedChatEvent>> ExecuteAsync(SessionState state, ExecuteToolCall tool, CancellationToken cancellationToken)
        => registry.GetRequired(tool.ToolName).ExecuteAsync(state, tool, cancellationToken);
}
