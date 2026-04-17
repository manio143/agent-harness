# Agent Harness (ACP) — C# reference implementation

A **test-first** C# implementation of the **Agent Client Protocol (ACP)** plus a small, reusable **agent harness** you can run locally.

If you want:
- a runnable ACP stdio server (`dotnet`)
- strong validation and predictable JSON-RPC errors
- tool calling (built-in tools + MCP tools)
- durable, event-sourced sessions (Observed → Committed)
- threading scenarios (parent/child threads + inbox delivery)

…this repo is that.

## What you can do with it

### Run an ACP agent locally
- Start the stdio server (`Agent.Server`)
- Connect with an ACP client (e.g. `acpx`)
- Prompt it against an OpenAI-compatible endpoint (Ollama supported)

### Use tools
The harness exposes a tool catalog to the model:
- **Built-in (capability-gated):**
  - `read_text_file`
  - `write_text_file`
  - `execute_command` (ACP terminal) with args: `{ "command": "<exe>", "args": ["..."] }`
- **Harness internal:** threading tools (`thread_*`), `report_intent`
- **MCP:** tools discovered from configured MCP servers are merged into the catalog (e.g. `everything__echo`).

### Persist sessions
Committed history is append-only JSONL with a small `session.json` projection. ACP publication is **committed-only**.

---

## Quickstart (manual E2E)

### 1) Build

```bash
cd /home/node/.openclaw/workspace/marian-agent
dotnet build Agent.slnx -c Release
```

### 2) Run via acpx

```bash
# acpx: https://github.com/openclaw/acpx
acpx --agent "dotnet src/Agent.Server/bin/Release/net8.0/Agent.Server.dll" --timeout 60 exec "Hello"
```

### 3) Configure provider (Ollama)
Edit:
- `src/Agent.Server/appsettings.json`

Typical values:
- `AgentServer:OpenAI:BaseUrl = http://ollama-api:11434/v1`
- `AgentServer:OpenAI:Model = qwen2.5:3b`

### Debugging
To see raw JSON-RPC traffic and prompt/observed logs:

```bash
AGENTSERVER_AgentServer__Logging__LogRpc=true \
AGENTSERVER_AgentServer__Logging__LogObservedEvents=true \
AGENTSERVER_AgentServer__Logging__LogLlmPrompts=true \
  acpx --agent "dotnet src/Agent.Server/bin/Release/net8.0/Agent.Server.dll" --timeout 300 exec "Hello"
```

---

## Samples

### Threading scenarios
- `samples/Threading.Scenarios/run_all.sh`

These scenarios validate parent/child thread behavior and determinism under tool-calling models.

### MCP demo
- `samples/Acp.EverythingMcpDemo/run.sh`

Runs a full tool chain including an MCP tool (`everything__echo`) and finishes with `DONE`.

---

## Repo layout

- `src/Agent.Acp/` — ACP transport + JSON-RPC + validation + server dispatch
- `src/Agent.Harness/` — harness (Observed → Committed reducer, session runner, tool lifecycle)
- `src/Agent.Server/` — runnable stdio ACP host wiring harness ↔ provider ↔ MCP
- `tests/` — unit + integration tests
- `docs/` — design + decision docs
- `schema/` — ACP schema assets + codegen outputs
- `scripts/` / `tools/` — schema/codegen helpers

## Tests

```bash
dotnet test Agent.slnx -c Release
```
