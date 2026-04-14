using Agent.Acp.Schema;

namespace Agent.Harness.Acp;

public static class ToolKindClassifier
{
    // ACP ToolKind values (string union): read, edit, delete, move, search, execute, think, fetch, switch_mode, other.
    // We only classify the built-in tools precisely for now; everything else defaults to "other".
    public static ToolKind ForToolName(string toolName)
        => toolName switch
        {
            "read_text_file" => new ToolKind(ToolKind.Read),
            "write_text_file" => new ToolKind(ToolKind.Edit),
            "execute_command" => new ToolKind(ToolKind.Execute),

            // MCP tools are too diverse to guess safely from name alone.
            // (e.g. everything__get_sum is compute-only; other servers may do writes.)
            _ => new ToolKind(ToolKind.Other),
        };
}
