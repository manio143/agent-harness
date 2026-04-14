using System.Collections.Immutable;
using Agent.Acp.Schema;
using Agent.Harness;
using Agent.Harness.Acp;

namespace Agent.Server;

public sealed class DefaultMcpDiscovery : IMcpDiscovery
{
    public Task<(ImmutableArray<ToolDefinition> Tools, IMcpToolInvoker Invoker)> DiscoverAsync(NewSessionRequest request, CancellationToken cancellationToken) =>
        McpDiscovery.DiscoverAsync(request, cancellationToken);
}
