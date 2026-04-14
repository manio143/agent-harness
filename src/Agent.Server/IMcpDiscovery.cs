using System.Collections.Immutable;
using Agent.Acp.Schema;
using Agent.Harness;
using Agent.Harness.Acp;

namespace Agent.Server;

public interface IMcpDiscovery
{
    Task<(ImmutableArray<ToolDefinition> Tools, IMcpToolInvoker Invoker)> DiscoverAsync(NewSessionRequest request, CancellationToken cancellationToken);
}
