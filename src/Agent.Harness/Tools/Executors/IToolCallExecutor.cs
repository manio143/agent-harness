using System.Collections.Immutable;

namespace Agent.Harness.Tools.Executors;

/// <summary>
/// Executes a specific subset of tool calls.
///
/// Tool execution is an imperative boundary; routing allows us to keep ACP host tools,
/// MCP tools, and harness/system tools separated.
/// </summary>
public interface IToolCallExecutor
{
    bool CanExecute(string toolName);

    Task<ImmutableArray<ObservedChatEvent>> ExecuteAsync(
        SessionState state,
        ExecuteToolCall tool,
        CancellationToken cancellationToken);
}
