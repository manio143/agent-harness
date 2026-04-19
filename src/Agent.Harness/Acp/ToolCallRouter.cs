using System.Collections.Immutable;

namespace Agent.Harness.Acp;

public sealed class ToolCallRouter
{
    private readonly ImmutableArray<IToolCallExecutor> _executors;

    public ToolCallRouter(IEnumerable<IToolCallExecutor> executors)
    {
        _executors = executors?.ToImmutableArray() ?? ImmutableArray<IToolCallExecutor>.Empty;
    }

    public Task<ImmutableArray<ObservedChatEvent>> ExecuteAsync(
        SessionState state,
        ExecuteToolCall tool,
        CancellationToken cancellationToken)
    {
        foreach (var ex in _executors)
        {
            if (ex.CanExecute(tool.ToolName))
                return ex.ExecuteAsync(state, tool, cancellationToken);
        }

        return Task.FromResult(ImmutableArray.Create<ObservedChatEvent>(new ObservedToolCallFailed(tool.ToolId, "unknown_tool")));
    }
}
