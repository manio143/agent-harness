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

### 3) Configure the agent

#### Configuration sources (in order)
`Agent.Server` loads configuration from:
1. `appsettings.json` (next to the built `Agent.Server.dll`)
2. **Environment variables** with prefix `AGENTSERVER_`
3. **dotnet user-secrets** (only when `DOTNET_ENVIRONMENT=Development`)

All examples below use the env var prefix + .NET `__` nesting convention.

#### Models / providers
Prefer the **friendly-name model catalog**:

- `AgentServer:Models:DefaultModel` (friendly name, default: `default`)
- `AgentServer:Models:QuickWorkModel` (friendly name, default: `default`)
- `AgentServer:Models:Catalog:<friendly>:{ BaseUrl, ApiKey, Model, ContextWindowK, NetworkTimeoutSeconds, MaxOutputTokens }`

Example (Ollama):

```bash
AGENTSERVER_AgentServer__Models__DefaultModel=ollama \
AGENTSERVER_AgentServer__Models__Catalog__ollama__BaseUrl=http://ollama-api:11434/v1 \
AGENTSERVER_AgentServer__Models__Catalog__ollama__ApiKey=ollama \
AGENTSERVER_AgentServer__Models__Catalog__ollama__Model=qwen2.5:3b \
  acpx --agent "dotnet src/Agent.Server/bin/Release/net8.0/Agent.Server.dll" --timeout 300 exec "Hello"
```

Example (Groq w/ explicit context window + completion cap):

```bash
AGENTSERVER_AgentServer__Models__DefaultModel=groq \
AGENTSERVER_AgentServer__Models__QuickWorkModel=groq \
AGENTSERVER_AgentServer__Models__Catalog__groq__BaseUrl=https://api.groq.com/openai/v1 \
AGENTSERVER_AgentServer__Models__Catalog__groq__ApiKey="$GROQ_API_KEY" \
AGENTSERVER_AgentServer__Models__Catalog__groq__Model=llama-3.1-8b-instant \
AGENTSERVER_AgentServer__Models__Catalog__groq__ContextWindowK=64 \
AGENTSERVER_AgentServer__Models__Catalog__groq__MaxOutputTokens=256 \
  acpx --agent "dotnet src/Agent.Server/bin/Release/net8.0/Agent.Server.dll" --timeout 300 exec "Hello"
```

> Back-compat: `AgentServer:OpenAI:*` is still supported, but new code should use `AgentServer:Models:*`.

#### Tool result capping (recommended for low-TPM providers)
Tool outputs can be capped **at the observed/committed event level** to reduce prompt bloat.

Settings:
- `AgentServer:ToolResultCapping:Enabled` (bool)
- `AgentServer:ToolResultCapping:MaxStringChars`
- `AgentServer:ToolResultCapping:MaxArrayItems`
- `AgentServer:ToolResultCapping:MaxObjectProperties`
- `AgentServer:ToolResultCapping:MaxDepth`

Example:

```bash
AGENTSERVER_AgentServer__ToolResultCapping__Enabled=true \
AGENTSERVER_AgentServer__ToolResultCapping__MaxStringChars=128 \
AGENTSERVER_AgentServer__ToolResultCapping__MaxArrayItems=10 \
AGENTSERVER_AgentServer__ToolResultCapping__MaxObjectProperties=20
```

If a **non-`read_text_file`** tool result is truncated, the harness writes the full raw output under:

- `.agent/sessions/<sessionId>/threads/<threadId>/raw_tool_results/tool-result-<guid>.json`

…and the capped tool result includes a pointer (`_raw_result_file`).

#### Threading / main thread capabilities
You can restrict the **main thread tool surface** using capability selectors (see `docs/design/11-thread-capabilities.md`).

Settings:
- `AgentServer:Threading:MainThreadCapabilities:Allow[]`
- `AgentServer:Threading:MainThreadCapabilities:Deny[]`

Selectors include:
- groups: `threads`, `fs.read`, `fs.write`, `host.exec`
- MCP: `mcp:*`, `mcp:<serverId>`, `mcp:<serverId>:*`, `mcp:<serverId>:<toolId>`

Env var example (deny shell + threading tools):

```bash
AGENTSERVER_AgentServer__Threading__MainThreadCapabilities__Deny__0=host.exec \
AGENTSERVER_AgentServer__Threading__MainThreadCapabilities__Deny__1=threads
```

#### Compaction
Settings:
- `AgentServer:Compaction:Threshold` (default: `0.90`)
- `AgentServer:Compaction:TailMessageCount` (default: `5`)
- `AgentServer:Compaction:MaxTailMessageChars` (optional)
- `AgentServer:Compaction:Model` (friendly name, default: `default`)

#### Logging
Settings:
- `AgentServer:Logging:LogRpc`
- `AgentServer:Logging:LogObservedEvents`
- `AgentServer:Logging:LogLlmPrompts`

Example:

```bash
AGENTSERVER_AgentServer__Logging__LogRpc=true \
AGENTSERVER_AgentServer__Logging__LogObservedEvents=true \
AGENTSERVER_AgentServer__Logging__LogLlmPrompts=true \
  acpx --agent "dotnet src/Agent.Server/bin/Release/net8.0/Agent.Server.dll" --timeout 300 exec "Hello"
```

#### ACP publication
Settings:
- `AgentServer:Acp:PublishReasoning` (default: `false`)

When enabled, the agent publishes reasoning deltas to ACP clients that support it.

#### Session storage
- `AgentServer:Sessions:Directory` (default: `.agent/sessions`)

The session store root is computed as:

`<acp client CWD>/<AgentServer:Sessions:Directory>`

So when you run `acpx --cwd <dir> ...`, sessions end up under `<dir>/.agent/sessions` by default.

#### User-secrets (local dev)
`Agent.Server` loads user-secrets only when `DOTNET_ENVIRONMENT=Development`.

Example:

```bash
cd src/Agent.Server
DOTNET_ENVIRONMENT=Development dotnet user-secrets set "AgentServer:Models:Catalog:groq:ApiKey" "$GROQ_API_KEY"
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
