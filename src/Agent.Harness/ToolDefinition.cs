namespace Agent.Harness;

/// <summary>
/// Tool definition for the agent's tool catalog.
/// 
/// RED: Placeholder type. Implementation driver will flesh this out with:
/// - Name (tool identifier)
/// - Description (for model)
/// - InputSchema (JSON Schema for parameters)
/// - Required capabilities
/// - Handler mapping
/// </summary>
using System.Text.Json;

public sealed record ToolDefinition(
    string Name,
    string Description,
    JsonElement InputSchema);

