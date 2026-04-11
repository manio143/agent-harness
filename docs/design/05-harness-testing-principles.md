# Harness Testing Principles (Functional Core + Imperative Shell)

This document captures the testing strategy for the experimental agent harness we’re building in this repo.

## Goals

- **Deterministic tests**: no real LLM calls in unit/integration tests.
- **Functional Core / Imperative Shell**:
  - Functional core owns *truth* (typed event log) and pure-ish transforms (rendering).
  - Imperative shell adapts to the outside world (ACP transport, streaming, UI) and is tested separately.
- **High leverage assertions**:
  - Prefer asserting **typed events** and **rendered prompts** over brittle internal call ordering.
- **Forward experimentation**:
  - Make it easy to add features like transcript rewriting, fork/branching, buffering vs pass-through streaming.

## Primary artifact: strongly-typed event log

We treat the session’s **append-only typed event stream** as the truth source.

Benefits:
- Auditable: you can replay, debug, and correlate tool calls/output.
- Testable: tests assert on events rather than private methods.
- Extensible: adding new behavior means adding new event types (or new projections).

### Debug events must be gated
Some events exist only to make tests and debugging easier (example: recording the exact rendered prompt messages).

Rule:
- Debug-only events MUST be **opt-in via configuration**, and disabled by default.

## Test seams

### 1) Functional core tests (fast, stable)
**Purpose:** validate core semantics without ACP or real transports.

We use a scripted chat client and an in-memory event log.

Assertions:
- Event log contains `UserMessageAdded("Hello")`.
- Chat client is invoked with rendered messages containing the user message.
- Event log contains `AssistantMessageAdded("Hello back")`.

### 2) Adapter tests (ACP integration without LLM)
**Purpose:** validate the imperative shell (ACP-facing) correctly translates core outputs to ACP.

We still use a scripted chat client (no LLM), but we run:
- `AcpAgentServer` + an in-memory transport
- `AcpClientConnection` to drive requests and observe `session/update` notifications

Assertions:
- `session/update` contains an `AgentMessageChunk` with the assistant text.
- final `session/prompt` response has `stopReason = end_turn`.

## Streaming design principles (model output vs user-visible streaming)

Streaming introduces a key choice:

- **Pass-through mode**: stream model output immediately to the user.
- **Buffered mode**: accumulate streamed output internally, allowing policy decisions (rewrite/redact/replace), then emit a final or rewritten stream.

We design for *both* by:

1. Capturing model deltas as events (e.g., `AssistantDeltaAppended(...)` in the future).
2. Separating:
   - **truth events** (what happened)
   - **projection** into user-visible updates (ACP `session/update` chunks)
3. Making the adapter choose a streaming policy:
   - immediate forward
   - buffer until “commit”

In tests, we can drive the core with scripted streaming events and assert that the adapter either:
- forwards chunks as they arrive, or
- withholds until commit, then emits a rewritten chunk.

## What we deliberately avoid

- Tests that assert exact internal call sequences across layers.
- Tests that depend on real model nondeterminism.
- Encoding “UX SHOULD” rules as hard failures (those stay out of core correctness tests).

## Current test inventory

- `tests/Agent.Harness.Tests/SessionCoreHelloTests.cs` (functional core)
- `tests/Agent.Harness.Tests/AcpAdapterHelloTests.cs` (ACP adapter)
