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
            "command": { "type": "string", "description": "Executable name (argv[0])" },
            "args": {
              "type": "array",
              "items": { "type": "string" },
              "description": "Argument vector (argv[1..])"
            }
          },
          "required": ["command"],
          "additionalProperties": false
        }
        """));

    public static ToolDefinition PatchTextFile { get; } = new(
        Name: "patch_text_file",
        Description: "Patch a UTF-8 text file by applying structured edits. The tool reads the file, applies edits sequentially, then writes it back.\n\nSupported edits (applied in order):\n- replace_exact: replace oldText with newText (requires unique match unless occurrence is provided)\n- insert_before: insert text immediately before anchorText\n- insert_after: insert text immediately after anchorText\n- delete_exact: delete text (requires unique match unless occurrence is provided)\n\nExamples:\n- {\"path\":\"README.md\",\"edits\":[{\"op\":\"replace_exact\",\"oldText\":\"old\",\"newText\":\"new\"}]}\n- {\"path\":\"a.txt\",\"expectedSha256\":\"...\",\"edits\":[{\"op\":\"insert_after\",\"anchorText\":\"class Foo\",\"text\":\"\\n// note\\n\"}]} ",
        InputSchema: ParseSchema("""
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "path": { "type": "string", "description": "Path to the file to patch" },
            "expectedSha256": { "type": "string", "description": "Optional SHA-256 of the current file; if provided it must match before applying edits" },
            "edits": {
              "type": "array",
              "minItems": 1,
              "items": {
                "oneOf": [
                  {
                    "type": "object",
                    "additionalProperties": false,
                    "properties": {
                      "op": { "const": "replace_exact" },
                      "oldText": { "type": "string" },
                      "newText": { "type": "string" },
                      "occurrence": { "type": "integer", "minimum": 0 }
                    },
                    "required": ["op", "oldText", "newText"]
                  },
                  {
                    "type": "object",
                    "additionalProperties": false,
                    "properties": {
                      "op": { "const": "insert_before" },
                      "anchorText": { "type": "string" },
                      "text": { "type": "string" },
                      "occurrence": { "type": "integer", "minimum": 0 }
                    },
                    "required": ["op", "anchorText", "text"]
                  },
                  {
                    "type": "object",
                    "additionalProperties": false,
                    "properties": {
                      "op": { "const": "insert_after" },
                      "anchorText": { "type": "string" },
                      "text": { "type": "string" },
                      "occurrence": { "type": "integer", "minimum": 0 }
                    },
                    "required": ["op", "anchorText", "text"]
                  },
                  {
                    "type": "object",
                    "additionalProperties": false,
                    "properties": {
                      "op": { "const": "delete_exact" },
                      "text": { "type": "string" },
                      "occurrence": { "type": "integer", "minimum": 0 }
                    },
                    "required": ["op", "text"]
                  }
                ]
              }
            }
          },
          "required": ["path", "edits"]
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
        Description: "List threads in the current session, including their current intent, status, and model.",
        InputSchema: ParseSchema("""
        { "type": "object", "properties": { }, "required": [ ] }
        """));

    public static ToolDefinition ThreadStart { get; } = new(
        Name: "thread_start",
        Description: "Start a child thread (new or fork from current thread) and attach an initial message. Optional: set the child's model.",
        InputSchema: ParseSchema("""
        {
          "type": "object",
          "properties": {
            "name": { "type": "string", "description": "Mandatory unique name/id for the new child thread (unique within the session). Use a short, stable identifier like 'research' or 'fix_deadlock'." },
            "context": { "type": "string", "enum": ["new", "fork"], "description": "Whether to start from empty state or fork the current thread" },
            "mode": { "type": "string", "enum": ["multi", "single"], "description": "Thread mode. multi: long-lived; can accept multiple tasks/messages. single: one-shot; thread is closed when it becomes idle with an empty inbox." },
            "message": { "type": "string", "description": "Initial message to attach to the child thread" },
            "delivery": { "type": "string", "enum": ["enqueue", "immediate"], "description": "Whether the message should be delivered immediately or enqueued until idle" },
            "model": { "type": "string", "description": "Optional model friendly name for the child thread (or 'default')" },
            "capabilities": {
              "type": "object",
              "properties": {
                "allow": { "type": "array", "items": { "type": "string" }, "description": "Capability allow selectors (e.g. 'fs.read', 'threads', 'mcp:*', 'mcp:everything', '*')" },
                "deny": { "type": "array", "items": { "type": "string" }, "description": "Capability deny selectors (deny wins)" }
              }
            }
          },
          "required": ["name", "context", "mode", "message"]
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
            "message": { "type": "string", "description": "Message to enqueue" },
            "delivery": { "type": "string", "enum": ["enqueue", "immediate"], "description": "Whether the message should be delivered immediately or enqueued until idle" }
          },
          "required": ["threadId", "message"]
        }
        """));

    public static ToolDefinition ThreadStop { get; } = new(
        Name: "thread_stop",
        Description: "Stop/close a thread so it can no longer receive messages and is removed from the thread list.",
        InputSchema: ParseSchema("""
        {
          "type": "object",
          "properties": {
            "threadId": { "type": "string", "description": "Thread id to stop" },
            "reason": { "type": "string", "description": "Optional reason for stopping" }
          },
          "required": ["threadId"]
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

    public static ToolDefinition ThreadConfig { get; } = new(
        Name: "thread_config",
        Description: "Get or set thread configuration (currently: model).",
        InputSchema: ParseSchema("""
        {
          "type": "object",
          "properties": {
            "threadId": { "type": "string", "description": "Thread id (defaults to current thread)" },
            "model": { "type": "string", "description": "Model friendly name to use for this thread (or 'default')" }
          },
          "required": [ ]
        }
        """));

    private static JsonElement ParseSchema(string json)
        => JsonDocument.Parse(json).RootElement.Clone();
}
