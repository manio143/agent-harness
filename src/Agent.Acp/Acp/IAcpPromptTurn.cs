namespace Agent.Acp.Acp;

/// <summary>
/// Per-prompt-turn services (scoped to a single session/prompt request).
/// </summary>
public interface IAcpPromptTurn
{
    IAcpToolCalls ToolCalls { get; }
}
