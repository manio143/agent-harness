# ACP Harness (C#) — Testing Approach & Early Design Decisions

## Goals

- **Verify core protocol behaviors** of an ACP-compatible agent harness without relying on a real CLI/client.
- Keep tests **fast, deterministic, and integration-oriented** (exercise real dispatch, correlation, cancellation).
- Add a small number of **stdio framing smoke tests** later (to validate newline-delimited JSON), without duplicating the entire behavior suite.

## Current Testing Level (what we do now)

### In-memory JSON-RPC integration tests

We currently test at the **JSON-RPC message object level**, in-process:

- A server is started with `AcpAgentServer.RunAsync(serverTransport)`.
- A client uses `AcpClientConnection` to send JSON-RPC requests and await responses.
- Client and server are connected via a pair of **in-memory transports** (`InMemoryTransport.CreatePair()`).
  - Under the hood: back-to-back `Channel<JsonRpcMessage>`.

This hits the system at the same seam we care about for correctness:

- JSON-RPC envelope parsing/serialization logic
- method dispatch (`initialize`, `session/new`, `session/prompt`, etc.)
- request/response correlation by id
- notification emission (`session/update`)
- cancellation (`session/cancel`) and prompt CTS wiring
- agent→client request round-trips (`client/readTextFile` etc.)

### What we are **not** testing yet

- OS/process boundary (`dotnet run` child process).
- Real stdio framing with bytes/streams (we will add a couple smoke tests).
- Full schema conformance for all DTOs (blocked by union-codegen quality).

## Behavioral invariants encoded in tests so far

- **Initialize contract:** `initialize` returns `protocolVersion`, `agentInfo`, `agentCapabilities`, `authMethods`.
- **Session updates:** `session/prompt` can emit multiple `session/update` notifications before returning a final response.
- **Ping-pong dependency:** agent waits for the client’s response to an agent-initiated request (verified by gating response with a `TaskCompletionSource`).
- **Cancellation:** `session/cancel` stops a running prompt (no hang until timeout).

## Stdio testing plan (minimal)

We will add a small suite of smoke tests that:

1. Send a line-delimited JSON-RPC request into `LineDelimitedStreamTransport` input stream.
2. Assert that exactly one JSON-RPC response line is written to output stream.
3. Assert `session/update` notifications produce additional lines.

We will **not** re-run all behavioral tests through stdio to avoid duplicating coverage.

## Design decisions (so far)

### Transport abstraction

- Provide an `ITransport` abstraction:
  - `SendMessageAsync(JsonRpcMessage)`
  - `ChannelReader<JsonRpcMessage> MessageReader`
- Current: stdio-style `LineDelimitedStreamTransport`.
- Future: allow custom transports (e.g., streamable HTTP) without changing dispatch logic.

### Server dispatch

- `AcpAgentServer`:
  - reads from `ITransport.MessageReader`
  - routes requests by `method`
  - sends responses and errors as JSON-RPC envelopes
  - resolves agent-initiated request responses using a pending-map (id → TCS)

### Agent API surface

- Baseline: `IAcpAgent` for simple agents.
- Extended: `IAcpAgentWithContext` exposes `IAcpAgentContext` during `session/prompt`:
  - `SendSessionUpdateAsync(...)`
  - `RequestAsync<TReq,TResp>(...)` (agent → client)

Rationale: keeps the simple surface small while enabling streaming updates and agent→client calls.

### Type generation

- Source of truth: ACP `schema.json` pulled from GitHub **Release assets**.
- Current generator: NJsonSchema-based tool (`tools/Agent.Acp.TypeGen`) with a preprocessing step converting `$defs` → `definitions`.
- Known issue: union-heavy types (`anyOf`/`oneOf`) are not yet strongly typed; we temporarily allow fallback “extension objects” in `Generated/AcpSchema.Patches.cs`.

## Next decisions to document

- Approach for **strongly typed discriminated unions** (schema preprocessing vs generator settings vs small handwritten union wrappers).
- Contract tests that validate exact JSON payload shapes for core methods (esp. protocol version format).
