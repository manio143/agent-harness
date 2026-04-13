# Decisions — Tool Calling MVP (ACP + MCP + MEAI)

Date: 2026-04-13

This document captures the *locked* decisions made while implementing Tool Calling MVP so future work doesn’t lose context.

## D1 — Tool execution model (MEAI integration)

**Problem:** MEAI can support tool calling in ways that either auto-invoke tools via callbacks or simply return tool-call intent. We need to preserve our architecture goals (rewrite after tool calls; functional core is the decision-maker).

**Options considered:**
1. **Mode A (chosen):** LLM proposes tool calls; harness parses tool-call intent; harness executes; then we may rewrite/continue the turn.
2. Mode B: MEAI (or a wrapper) auto-invokes `AIFunction` callbacks.

**Decision:** Use **Mode A**.

**Rationale:** We may want to rewrite the LLM input/transcript after a tool call in a non-standard way. Auto-invocation makes that harder and can violate the “core decides” boundary.

**Consequences:**
- We keep an independent `ToolDefinition` model.
- We only convert into provider-specific tool formats at the boundary, if needed.
- No “function-invoking chat client” middleware for MVP.

## D2 — Functional core is the only committer (Effects/Outbox)

**Problem:** Tool calling requires I/O and streaming updates, but we want a pure reducer and an auditable committed log.

**Options considered:**
1. Tools executed inside reducer (reject).
2. Reducer emits *effects* and SessionRunner executes them (chosen).

**Decision:** Reducer returns `(CommittedEvents, Effects)`; SessionRunner executes effects and feeds results back as *Observed* events.

**Rationale:** Preserves functional purity + makes all externally visible behavior derivable from committed events.

## D3 — Capability gating vs permission prompts

**Problem:** ACP supports `session/request_permission`. For MVP we want minimal friction and correctness.

**Options considered:**
1. Prompt user/ACP client for permission per tool call.
2. Capability-only gating (chosen).

**Decision:** MVP uses **capability-only gating**.

**Meaning:**
- `CheckPermission` remains as a policy hook, but resolves deterministically (approve/deny) based on negotiated capabilities + tool existence + args validation.
- No ACP `session/request_permission` calls for MVP.

**Consequences:** We keep the architecture ready for future interactive approvals without changing the reducer contract.

## D4 — Tool names

**Problem:** Tool name character constraints differ across providers. We need portability.

**Research outcome:** OpenAI tool/function names are constrained to `^[a-zA-Z0-9_-]{1,64}$` (no `:`/`;`).

**Decision:** Tool names are **snake_case** only.

**MCP namespacing:** MCP tools are exposed as `{server}__{tool}` (double underscore), both normalized to snake_case.

## D5 — Tool definitions exposed to LLM

**Problem:** Should we advertise tools the session cannot execute?

**Options considered:**
1. Always expose tools; reject at runtime.
2. **Expose only runnable tools** (chosen).

**Decision:** If required client capability is absent, we **do not provide** the tool definition to the model.

## D6 — ToolDefinition model

**Problem:** We need a provider-neutral representation that can map to ACP/MCP/MEAI.

**Decision:** `ToolDefinition` includes:
- `Name`
- `Description`
- `InputSchema` (JSON schema, stored as `JsonElement`/`JsonDocument`)

**Notes:**
- Built-in tools may be authored with MEAI attributes and *projected* into `ToolDefinition`.
- MCP tools come from `tools/list` and are projected into `ToolDefinition`.

## D7 — Tool call lifecycle events

**Problem:** What states/events do we need to represent tool execution + streaming?

**Decision:** Keep explicit lifecycle events (committed):
- `ToolCallRequested`
- `ToolCallPermissionApproved(reason)` / `ToolCallPermissionDenied(reason)`
- `ToolCallPending`
- `ToolCallInProgress`
- `ToolCallUpdate` (rename: no `*Committed` suffix)
- Terminal: `ToolCallCompleted` / `ToolCallFailed` / `ToolCallRejected` / `ToolCallCancelled`

**Rationale:** Supports ACP UI streaming and makes debugging/replay easier.

## D8 — Early rejection

**Problem:** Tool availability is ephemeral; model can ask for unknown tool or invalid args.

**Decision:** It must be possible to fail at request time:
- unknown tool → `ToolCallRejected(reason="unknown_tool")`
- args invalid vs schema → `ToolCallRejected(reason="invalid_args")`

## D9 — MCP discovery timing and ephemerality

**Problem:** MCP tool availability can change per session/reload; we need a stable UX and correct tool catalog.

**Decision:** MCP discovery happens **eagerly during `session/new`**.

**Notes:**
- Tool availability is **ephemeral per session**.
- MCP discovery is not driven by a reducer effect in MVP.

## D10 — ACP streaming source of truth

**Problem:** ACP clients rely on `session/update` ordering and additivity.

**Decision:** ACP publishes `session/update` derived from the committed stream only.

**Implementation note:** `session/update` is wrapped as `{ sessionId, update }`.

## D11 — Responsibility split: Server vs Harness (Core vs Shell)

**Problem:** Where should provider-specific parsing/normalization live (e.g., MEAI `ChatResponseUpdate` → harness `Observed*` events), and what is the correct responsibility split between `Agent.Server` and `Agent.Harness`?

**Decision:**
- `Agent.Harness` is split into:
  - **Functional core**: reducer/state/committed events/effects contracts.
  - **Imperative shell**: MEAI streaming integration + normalization into `ObservedChatEvent`s.
- `Agent.Server` is the **composition root / executable host**:
  - provides the concrete `Microsoft.Extensions.AI.IChatClient` implementation (OpenAI/Ollama/etc)
  - hosts ACP JSON-RPC transport + config + logging

**Concretely (current code):**
- MEAI normalization lives in `Agent.Harness` under `src/Agent.Harness/Llm/*`.
- LLM calling is performed by the harness-owned ACP session agent (`HarnessAcpSessionAgent`) which depends directly on `Microsoft.Extensions.AI.IChatClient`.

**Rationale:**
- The harness **cannot function without an LLM** and MEAI is the chosen abstraction; we embrace it in the shell.
- Keeping “when to call the model” in the harness avoids a server-owned control loop that would need to be rewritten as tool calling expands (multi-call turns).
- Avoids over-abstracting the model call: we normalize MEAI *responses* (stream → observed events) but don’t hide the model invocation behind an extra interface.

**Consequences:**
- The server becomes thin wiring: swap model providers by swapping the registered `IChatClient`.
- The harness owns the control-loop evolution needed for Mode A (tool call intent → execute → re-prompt).
- MEAI types are allowed in the harness shell, but remain out of the reducer/core.

---

## Test/Implementation notes

- ACP tool-call lifecycle tests were updated to correctly unwrap the `session/update` envelope.
- MCP integration tests are deterministic and use an in-memory fake MCP server (no processes).

