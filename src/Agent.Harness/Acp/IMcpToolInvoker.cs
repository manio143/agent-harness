using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Agent.Harness.Acp;

public interface IMcpToolInvoker
{
    bool CanInvoke(string toolName);

    Task<JsonElement> InvokeAsync(string toolId, string toolName, object args, CancellationToken cancellationToken);
}

public sealed class NullMcpToolInvoker : IMcpToolInvoker
{
    public static NullMcpToolInvoker Instance { get; } = new();

    public bool CanInvoke(string toolName) => false;

    public Task<JsonElement> InvokeAsync(string toolId, string toolName, object args, CancellationToken cancellationToken)
        => throw new NotSupportedException("MCP tool invocation not configured.");
}
