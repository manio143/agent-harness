# Acp.EverythingMcpDemo

This sample configures `acpx` to talk to this repo's ACP server and attach the **MCP Everything** stdio server.

## Prereqs

- `dotnet build Agent.slnx -c Release`
- `node` + `npx` available

## Run

From repo root:

```bash
cd samples/Acp.EverythingMcpDemo

# Start a new ACP session (this will also connect to MCP and eager-list tools during session/new)
npx -y acpx@latest --cwd . sessions new --name demo

# Prompt the agent
npx -y acpx@latest --cwd . prompt --session demo "Hello"
```

Suggested prompt for a full demo:

> Create /tmp/demo.txt with "hello". Read it back. Run `uname -a`. Then call an MCP tool from the everything server and summarize.

Notes:
- MCP tools will appear as `everything__<tool_name>` (snake_case). Use `acpx sessions tools --session demo` to see the exact names.
