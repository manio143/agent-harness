# Shell.McpProxyCmdletsDemo

This sample demonstrates exposing MCP tools as PowerShell proxy cmdlets inside `agent_shell_execute`.

It attaches the **MCP Everything** stdio server and relies on the harness to generate and import:
- A module per MCP server: `Mcp.everything`
- Proxy functions such as `Get-Sum` (from MCP tool `get_sum`)

## Prereqs

- `dotnet build Agent.slnx -c Release`
- `node` + `npx`

## Run

From repo root:

```bash
bash samples/Shell.McpProxyCmdletsDemo/run.sh
```

The run is deterministic and ends with exactly `DONE`.
