# PowerShell Shell + project: Drive Demo

This sample demonstrates:

- `agent_shell_execute` (PowerShell Core in-process shell)
- Per-thread shell state persistence (variables)
- `sandbox:` drive for agent-local working dir
- `project:` drive mapping directly to the session cwd with normal filesystem navigation

The run is designed to be deterministic and end with exactly `DONE`.

## Run

From repo root:

```bash
export ACP_TIMEOUT=1500
export AGENTSERVER_AgentServer__OpenAI__NetworkTimeoutSeconds=1500

bash samples/Shell.PowerShellClientDriveDemo/run.sh
```

Optional debug logging:

```bash
export AGENTSERVER_AgentServer__Logging__LogRpc=true
export AGENTSERVER_AgentServer__Logging__LogObservedEvents=true
export AGENTSERVER_AgentServer__Logging__LogLlmPrompts=true
```
