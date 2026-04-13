namespace Agent.Harness;

/// <summary>
/// Effects represent side-effect requests emitted by the functional core reducer.
/// The SessionRunner (imperative shell) executes these effects and feeds observations back to the reducer.
///
/// Invariant: The core reducer NEVER executes side effects directly; it only emits Effect values.
/// </summary>
public abstract record Effect;

/// <summary>
/// Request permission check for a tool call.
/// SessionRunner should invoke IAcpClientCaller.RequestPermissionAsync and feed the result back as an observation.
/// </summary>
public sealed record CheckPermission(
    string ToolId,
    string ToolName,
    object Args) : Effect;

/// <summary>
/// Execute a tool call (after permission granted).
/// SessionRunner should route to appropriate executor (filesystem, terminal, MCP) and stream observations back.
/// </summary>
public sealed record ExecuteToolCall(
    string ToolId,
    string ToolName,
    object Args) : Effect;

/// <summary>
/// Discover MCP tools from a server.
/// SessionRunner should invoke MCP tools/list and feed discovered tools as observations.
/// </summary>
public sealed record DiscoverMcpTools(
    string ServerId,
    object McpServerConfig) : Effect;
