using System.Text.Json;

namespace Agent.Harness;

public static class ToolSchemas
{
    public static ToolDefinition ReadTextFile { get; } = new(
        Name: "read_text_file",
        Description: "Read a UTF-8 text file from the client filesystem.",
        InputSchema: ParseSchema("""
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Path to the file to read" }
          },
          "required": ["path"]
        }
        """));

    public static ToolDefinition WriteTextFile { get; } = new(
        Name: "write_text_file",
        Description: "Write a UTF-8 text file to the client filesystem.",
        InputSchema: ParseSchema("""
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Path to the file to write" },
            "content": { "type": "string", "description": "Content to write" }
          },
          "required": ["path", "content"]
        }
        """));

    public static ToolDefinition ExecuteCommand { get; } = new(
        Name: "execute_command",
        Description: "Execute a terminal command via the client terminal capability.",
        InputSchema: ParseSchema("""
        {
          "type": "object",
          "properties": {
            "command": { "type": "string", "description": "Command to execute" }
          },
          "required": ["command"]
        }
        """));

    private static JsonElement ParseSchema(string json)
        => JsonDocument.Parse(json).RootElement.Clone();
}
