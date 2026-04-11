# Agent (ACP Harness in C#)

A minimal **Agent Client Protocol (ACP)** harness/library for writing ACP-compatible agents in C#.

## Goals

- Provide a small **library** that handles:
  - JSON-RPC envelopes
  - transport abstraction (today: stdio; future: custom transports)
  - request/notification dispatch for core ACP methods
- Let *your agent* implement a small interface (`IAcpAgent`).
- Keep the code small and testable; inspired by patterns in the MCP C# SDK.

## Repo layout

- `src/Agent.Acp/` — library
- `tests/Agent.Acp.Tests/` — xUnit integration-style tests
- `schema/` — ACP schema assets pulled from GitHub Releases
- `scripts/` — schema fetch + codegen helpers
- `tools/Agent.Acp.TypeGen/` — codegen helper (NJsonSchema-based)

## Schema update workflow

Fetch the latest release assets (`schema.json`, `meta.json`, plus unstable variants if present):

```bash
python3 scripts/fetch_latest_acp_assets.py --out schema
```

Build a codegen-friendly schema (`schema/schema.codegen.json`) and generate C# DTOs:

```bash
python3 scripts/build_codegen_schema.py
./scripts/generate_acp_types.sh
```

> Note: We keep **NSwag** as a local tool (see `.config/dotnet-tools.json`).
> ACP’s schema is `$defs`-heavy and current NSwag CLI codegen didn’t emit all reachable types.
> The generator we use is **NJsonSchema** (the same underlying library NSwag uses), via
> `tools/Agent.Acp.TypeGen`.

## Using the library (agent side)

Implement `IAcpAgent` and run an `AcpAgentServer` over a transport.

Today the main transport is newline-delimited JSON over streams (`LineDelimitedStreamTransport`),
which matches the typical **stdio** JSON-RPC framing.

## Tests

```bash
dotnet test Agent.slnx -c Release
```

The integration test spins up an in-memory client/server transport pair and validates
high-level protocol behavior:

- `initialize` request returns a valid response
- `session/new` request returns a session id

## Status

This is intentionally v0/minimal.

- ✅ JSON-RPC envelope + converter
- ✅ transport abstraction (stdio + in-memory test transport)
- ✅ agent-side dispatch for `initialize`, `authenticate` (optional), `session/new`, `session/prompt`, `session/cancel` (notification)
- ✅ xUnit integration tests

Next obvious steps:
- expand method coverage from ACP schema (session/list, load/save, etc.)
- improve DTO generation (remove `AcpSchema.Patches.cs` fallbacks)
- add stdio end-to-end smoke test (spawn a process, talk over pipes)
