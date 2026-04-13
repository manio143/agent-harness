namespace Agent.Harness;

/// <summary>
/// Effects represent side-effect requests emitted by the functional core reducer.
/// The SessionRunner (imperative shell) executes these effects and feeds observations back to the reducer.
///
/// Invariant: The core reducer NEVER executes side effects directly; it only emits Effect values.
/// </summary>
public abstract record Effect;

/// <summary>
/// Request a policy check for a tool call.
/// MVP decision: capability-only gating (no ACP session/request_permission).
/// SessionRunner resolves this deterministically (approve/deny + reason) and feeds the result back as an observation.
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

