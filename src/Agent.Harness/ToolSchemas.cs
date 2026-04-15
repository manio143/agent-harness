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

    // --- Harness internal coordination tools ---

    public static ToolDefinition ReportIntent { get; } = new(
        Name: "report_intent",
        Description: "Report the thread's current intent (short, single sentence). Must be called before other tools.",
        InputSchema: ParseSchema("""
        {
          "type": "object",
          "properties": {
            "intent": { "type": "string", "description": "Short sentence describing what you are trying to do" }
          },
          "required": ["intent"]
        }
        """));

    public static ToolDefinition ThreadList { get; } = new(
        Name: "thread_list",
        Description: "List threads in the current session, including their current intent and status.",
        InputSchema: ParseSchema("""
        { "type": "object", "properties": { }, "required": [ ] }
        """));

    public static ToolDefinition ThreadNew { get; } = new(
        Name: "thread_new",
        Description: "Create a new empty child thread and attach an initial message from the parent.",
        InputSchema: ParseSchema("""
        {
          "type": "object",
          "properties": {
            "message": { "type": "string", "description": "Initial message to attach to the new thread" }
          },
          "required": ["message"]
        }
        """));

    public static ToolDefinition ThreadFork { get; } = new(
        Name: "thread_fork",
        Description: "Fork the current thread into a child thread by cloning state and attaching an initial message.",
        InputSchema: ParseSchema("""
        {
          "type": "object",
          "properties": {
            "message": { "type": "string", "description": "Initial message to attach to the child thread" }
          },
          "required": ["message"]
        }
        """));

    public static ToolDefinition ThreadSend { get; } = new(
        Name: "thread_send",
        Description: "Send a message to another thread by enqueuing it in that thread's inbox.",
        InputSchema: ParseSchema("""
        {
          "type": "object",
          "properties": {
            "threadId": { "type": "string", "description": "Target thread id" },
            "message": { "type": "string", "description": "Message to enqueue" }
          },
          "required": ["threadId", "message"]
        }
        """));

    public static ToolDefinition ThreadRead { get; } = new(
        Name: "thread_read",
        Description: "Read assistant messages from another thread.",
        InputSchema: ParseSchema("""
        {
          "type": "object",
          "properties": {
            "threadId": { "type": "string", "description": "Thread id to read from" }
          },
          "required": ["threadId"]
        }
        """));

    private static JsonElement ParseSchema(string json)
        => JsonDocument.Parse(json).RootElement.Clone();
}
