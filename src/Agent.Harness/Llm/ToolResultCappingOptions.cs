namespace Agent.Harness.Llm;

/// <summary>
/// Configuration for tool result capping/truncation.
///
/// IMPORTANT: do not read environment variables directly. This should be populated via IOptions binding
/// in the host (e.g., Agent.Server) and passed into the harness.
/// </summary>
public sealed class ToolResultCappingOptions
{
    public bool Enabled { get; set; } = false;

    public int? MaxStringChars { get; set; }
    public int? MaxArrayItems { get; set; }
    public int? MaxObjectProperties { get; set; }
    public int? MaxDepth { get; set; }
}
