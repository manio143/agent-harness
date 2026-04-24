using System.Collections.Immutable;

namespace Agent.Harness.Tools.Executors;

public sealed class ToolCallRouter
{
    private readonly ImmutableArray<IToolCallExecutor> _executors;
    private readonly Func<SessionState, ExecuteToolCall, bool>? _gate;

    public ToolCallRouter(IEnumerable<IToolCallExecutor> executors, Func<SessionState, ExecuteToolCall, bool>? gate = null)
    {
        _executors = executors?.ToImmutableArray() ?? ImmutableArray<IToolCallExecutor>.Empty;
        _gate = gate;
    }

    public Task<ImmutableArray<ObservedChatEvent>> ExecuteAsync(
        SessionState state,
        ExecuteToolCall tool,
        CancellationToken cancellationToken)
    {
        if (_gate is not null && !_gate(state, tool))
            return Task.FromResult(ImmutableArray.Create<ObservedChatEvent>(
                new ObservedToolCallFailed(tool.ToolId, $"tool_not_allowed:{tool.ToolName}")));

        foreach (var ex in _executors)
        {
            if (ex.CanExecute(tool.ToolName))
                return ex.ExecuteAsync(state, tool, cancellationToken);
        }

        return Task.FromResult(ImmutableArray.Create<ObservedChatEvent>(new ObservedToolCallFailed(tool.ToolId, "unknown_tool")));
    }
}
