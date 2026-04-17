# ACP Patch Text File Demo

This sample demonstrates the built-in `patch_text_file` tool.

It exercises the intended workflow:

1. `report_intent`
2. `write_text_file` (create initial content)
3. `read_text_file` (returns `{ content, sha256 }`)
4. `patch_text_file` with `expectedSha256` + structured edits
5. `read_text_file` again to verify the new content

The run is designed to be deterministic and to end with exactly `DONE`.

## Run

From repo root:

```bash
export ACP_TIMEOUT=1500
export AGENTSERVER_AgentServer__OpenAI__NetworkTimeoutSeconds=1500

bash samples/Acp.PatchTextFileDemo/run.sh
```

Optional debug logging:

```bash
export AGENTSERVER_AgentServer__Logging__LogRpc=true
export AGENTSERVER_AgentServer__Logging__LogObservedEvents=true
export AGENTSERVER_AgentServer__Logging__LogLlmPrompts=true
```
