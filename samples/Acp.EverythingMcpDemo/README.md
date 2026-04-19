# Acp.EverythingMcpDemo

This sample configures `acpx` to talk to this repo's ACP server and attach the **MCP Everything** stdio server.

Note: Everything server requires an explicit transport argument (we pass `stdio`).

Also, the current Everything server package expects `ajv` to be resolvable at runtime (peer dependency). We run it via:

- `npx -p ajv -p @modelcontextprotocol/server-everything mcp-server-everything stdio`

…so the peer dep is present in the npx sandbox.

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

> First, switch inference model with `/set-model default` (or another configured friendly name).
> Then: Create an **absolute** path under this sample's cwd (e.g. `/home/node/.openclaw/workspace/marian-agent/samples/Acp.EverythingMcpDemo/demo.txt`) with "hello" (relative paths like `./demo.txt` also work; agent normalizes them). Read it back. Run `uname -a`. Then call an MCP tool from the everything server.
>
> For example: call `everything__get_sum` with `{ "a": 40, "b": 2 }` and summarize the result.

Notes:
- MCP tools will appear as `everything__<tool_name>` (snake_case). Use `acpx sessions tools --session demo` to see the exact names.
