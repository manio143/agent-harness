# Thread Inbox & Prompt Promotion (Per-Thread Logs)

## Goal

Enable **multiple threads inside a session** while preserving the harness’s core architectural invariant:

> **Committed events are produced only by the functional-core reducer in response to observed events.**

This document specifies:
- per-thread persistence model,
- inbox semantics (intake + delivery modes),
- the "promotion" step that turns inbox envelopes into first-class prompt events,
- the required execution abstraction to ensure main and child threads behave identically.

## Key decisions

### 1) Per-thread committed event logs (no session-level log)

- Each thread has its **own committed event log**.
- The **main thread** is the one bound to ACP user I/O; it is not special from a persistence standpoint.
- Thread tools (`thread_read`) read from the target thread’s committed log.

### 2) Inbox is the universal intake for all inbound messages

Every inbound message first becomes an inbox envelope:
- ACP user prompts
- inter-thread messages (`thread_send`, initial messages from `thread_start`)
- internal notifications (e.g., child became idle)

The inbox is modeled as committed events and is replayable/deterministic.

### 3) Inbox delivery modes

Each inbox envelope has `Delivery`:

- `Immediate`:
  - eligible for delivery on the next prompt boundary
  - **user prompts are always `Immediate`**
  - **idle notifications are generally `Immediate`**

- `Enqueue`:
  - eligible only when the *target thread* is `Idle`
  - **inter-thread messages may be `Immediate` or `Enqueue`** (chosen by the sender)

Delivery mode affects **when** an envelope becomes deliverable; it does not change how it is represented once promoted.

### 4) Typed inbox envelopes (no string parsing)

Inbox events carry a typed kind and optional metadata:

- `ThreadInboxMessageEnqueued` includes:
  - `Kind: ThreadInboxMessageKind`
  - `Meta?: ImmutableDictionary<string,string>`

The kind is domain-level and must be sufficient for promotion without parsing `Text`.

Recommended kinds:
- `UserPrompt`
- `InterThreadMessage`
- `ThreadIdleNotification`

### 5) Promotion: inbox -> first-class prompt events

The LLM prompt is built from **first-class committed events**, not from inbox events directly.

At each model-call boundary for a thread, the system:
1) selects deliverable inbox envelopes deterministically
2) commits a dequeue marker
3) commits a first-class event that becomes part of the thread’s prompt/history

This yields deterministic, replayable prompt rendering.

### 6) First-class message events (avoid generic SystemMessage)

Domain logic avoids a generic `SystemMessage` event.

The first-class message events used for prompt rendering are:
- `UserMessage`
- `InterThreadMessage`
- `ThreadIdleNotification`

**Renderer policy** maps domain events to LLM roles:
- `UserMessage` -> user role
- `InterThreadMessage` -> system role
- `ThreadIdleNotification` -> system role

(Exact text formatting is a renderer concern, not a domain event.)

### 7) Dequeue markers

When an envelope is consumed for a prompt boundary, commit:
- `ThreadInboxMessageDequeued(threadId, envelopeId, dequeuedAtIso, ...)`

Dequeue markers are used to compute pending inbox state:

`pending = Enqueued - Dequeued` (by `EnvelopeId`).

### 8) Observed -> committed invariant (threads)

All commits must originate from reducer output.

Therefore:
- ACP prompt arrival must be represented as a thread-scoped **observed** event (e.g., `ObservedInboxArrived(UserPrompt, ...)`).
- Tool execution that results in inter-thread messaging must also emit **observed** inbox-arrival events.
- The reducer decides which committed events to append:
  - `ThreadInboxMessageEnqueued`
  - dequeue markers
  - promoted first-class prompt events (`UserMessage`, `InterThreadMessage`, `ThreadIdleNotification`)

Imperative code must not append committed inbox/promoted events directly.

### 9) Consistent execution abstraction for main + child threads

Main and child threads must run through the same turn execution abstraction.

Reason: without a shared runner, main may support multi-step continuation (model -> tools -> model) while child does not.

Required abstraction:
- a per-thread "turn runner" that:
  - calls model (stream)
  - reduces observed events
  - executes non-`CallModel` effects after stream completion
  - continues model calls as needed (tool-call continuation)
  - enforces: **no overlapping `CallModel` per thread**

## Determinism requirements

- Inbox deliverability is a pure function of committed state + thread status.
- Promotion/dequeue must be deterministic (stable ordering):
  - order by `EnqueuedAtIso`, then `EnvelopeId`.
- Replay/resume must produce identical prompt inputs for the same committed history.

## Testing strategy

### A) Functional core (unit) tests

Reducer tests should assert:
- observed inbox arrival -> committed `ThreadInboxMessageEnqueued`
- prompt boundary observation -> committed dequeues + promoted first-class events
- delivery mode gating (`Immediate` vs `Enqueue` + thread idle status)
- idempotence: dequeued envelopes are not promoted again

These tests must not depend on ACP or a real `IChatClient`.

### B) Harness integration tests (fake streaming chat client)

Add end-to-end tests validating behavior via the public harness edge:
- user prompt arrives, flows through inbox, is promoted, and appears in the prompt sent to the chat client
- inter-thread messages respect `Immediate` vs `Enqueue`
- child idle notification is emitted only when the child is fully idle and is promoted on the parent’s next prompt boundary

## Non-goals (for this phase)

- Surfacing inbox lifecycle events to ACP clients as user-facing UX.
- Persisting a separate inbox.jsonl store (inbox is derived from committed events).
