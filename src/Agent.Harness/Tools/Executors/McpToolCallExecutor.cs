using System.Collections.Immutable;
using System.Text.Json;
using Agent.Harness.Acp;

namespace Agent.Harness.Tools.Executors;

public sealed class McpToolCallExecutor : IToolCallExecutor
{
    private readonly IMcpToolInvoker _mcp;

    public McpToolCallExecutor(IMcpToolInvoker mcp)
    {
        _mcp = mcp ?? NullMcpToolInvoker.Instance;
    }

    public bool CanExecute(string toolName) => _mcp.CanInvoke(toolName);

    public async Task<ImmutableArray<ObservedChatEvent>> ExecuteAsync(
        SessionState state,
        ExecuteToolCall tool,
        CancellationToken cancellationToken)
    {
        var payload = await _mcp.InvokeAsync(tool.ToolId, tool.ToolName, tool.Args, cancellationToken).ConfigureAwait(false);
        return ImmutableArray.Create<ObservedChatEvent>(new ObservedToolCallCompleted(tool.ToolId, payload));
    }
}
