# agent-harness (ACP + Harness in C#)

A **test-first** C# implementation of the **Agent Client Protocol (ACP)** plus a small, reusable **agent harness**.

This repo has three layers:

- **`Agent.Acp`**: ACP transport + JSON-RPC + strict validation + server dispatch.
- **`Agent.Harness`**: functional-core / imperative-shell harness for streaming chat providers (Observed → Committed event model).
- **`Agent.Server`**: runnable stdio ACP host wiring the harness to a provider (Ollama via OpenAI-compatible endpoint).

## Repo layout

- `src/Agent.Acp/` — ACP implementation (schema DTOs, server/client, transports)
- `src/Agent.Harness/` — provider-agnostic harness (Observed/Committed events, reducer, session runner)
- `src/Agent.Server/` — runnable stdio agent host (`dotnet run …`)
- `tests/Agent.Acp.Tests/` — ACP integration-style tests
- `tests/Agent.Harness.Tests/` — harness unit/integration tests
- `schema/` — ACP schema assets + codegen outputs
- `scripts/` — schema fetch + codegen helpers
- `tools/` — codegen helpers

## Key design points

### Strict ACP validation + consistent JSON-RPC error mapping

- Spec/parameter violations ⇒ **`-32602`**
- Unsupported optional methods ⇒ **`-32601`**
- Not initialized ⇒ **`-32000`** with: `"Connection not initialized. Call initialize first."`

### Harness: Observed vs Committed

Providers stream **Observed** updates (lossless, may contain raw provider payload). The harness reducer turns those into **Committed** events (stable history). ACP publishes **committed only**.

### Session persistence

Committed events can be stored as append-only **JSONL** and metadata (`session.json`) is a **projection of committed events** (e.g. `SessionTitleSet`).

## Running the stdio server (manual E2E)

The server uses MEAI + OpenAI-compatible endpoint (works with Ollama).

```bash
cd /home/node/.openclaw/workspace/marian-agent

dotnet build src/Agent.Server/Agent.Server.csproj -c Release

# Run with acpx (https://github.com/openclaw/acpx)
acpx --agent "dotnet src/Agent.Server/bin/Release/net8.0/Agent.Server.dll" --timeout 60 exec "Hello"
```

Configuration is in `src/Agent.Server/appsettings.json`.

To debug raw JSON-RPC traffic (stderr):

```bash
AGENTSERVER_AgentServer__Logging__LogRpc=true \
  acpx --agent "dotnet src/Agent.Server/bin/Release/net8.0/Agent.Server.dll" --timeout 60 exec "Hello"
```

## Tests

```bash
dotnet test Agent.slnx -c Release
```

## Schema update workflow

Fetch the latest ACP release assets (`schema.json`, `meta.json`, plus unstable variants if present):

```bash
python3 scripts/fetch_latest_acp_assets.py --out schema
```

Build a codegen-friendly schema (`schema/schema.codegen.json`) and generate C# DTOs:

```bash
python3 scripts/build_codegen_schema.py
./scripts/generate_acp_types.sh
```

> We use NJsonSchema via `tools/Agent.Acp.TypeGen` and keep generated DTO edits regen-safe via schema normalization + post-processing.
