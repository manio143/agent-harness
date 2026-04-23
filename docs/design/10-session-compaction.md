# 10 — Session Compaction

## Goal

When a thread’s conversation approaches the model’s context limit, we must compact prior history into a durable summary so subsequent `call_model` operations remain reliable.

**Key constraints (hard requirements):**

- Context limits are **not discoverable** via Microsoft.Extensions.AI (MEAI), so we must store them in `ModelCatalog`.
- Compaction is triggered based on **provider-reported token usage** (`totalTokens`) crossing a **threshold** (default 90%).
- If the provider model’s limit is **unknown**, we do **not** auto-compact.
- Compaction is **independent per thread**.
- Compaction must **end the current turn** and the compaction event is the **only committed event allowed between `TurnEnded` and the next `TurnStarted`**.
- Compaction prompt uses **event-log content only** (no injected system messages like model catalog/session metadata).
- Compaction model call uses **no tools**.
- Tool-call ordering between messages must be preserved.
- After compaction, the next prompt must include:
  1) compacted summary as a **system** message
  2) a tail of the last **N** messages verbatim (default 5)
  3) if compaction happened after tool calls but before an assistant message, we must include those tool calls/results verbatim in the tail so the agent can continue.

---

## Configuration

### ModelCatalog: context window
Add a context window size per catalog entry.

- `contextWindowK` is an integer in **thousands of tokens**.
  - Example: `4` means ~4000 tokens.

Suggested schema (shape only):

```jsonc
{
  "AgentServer": {
    "Models": {
      "DefaultModel": "groq",
      "Catalog": {
        "groq": {
          "model": "qwen/qwen3-32b",
          "contextWindowK": 32
        }
      }
    }
  }
}
```

**Important:** triggering uses the provider model name stored on committed `token_usage` events (`providerModel`). We must be able to map `providerModel -> catalog entry` to get `contextWindowK`. If mapping fails, limit is treated as unknown.

### Harness options
Introduce harness options:

- `CompactionThreshold` (double, default `0.90`)
- `CompactionTailMessageCount` (int, default `5`)
- `CompactionModel` (friendly name; default to `DefaultModel`)

---

## Triggering compaction

Compaction is evaluated when token usage is committed for the thread.

Inputs:
- `usage.totalTokens` (required)
- `providerModel` (required)
- `contextWindowK` from catalog (required; otherwise **no compaction**)

Computation:

```
limit = contextWindowK * 1000
ratio = usage.totalTokens / limit
if ratio >= CompactionThreshold => compaction due
```

**Unknown limit:** If `providerModel` does not resolve to a catalog entry with a `contextWindowK`, do nothing.

---

## Turn boundary + ordering invariants

### Invariant: compaction is a boundary between turns
Compaction must force a turn to end and must be the only committed event between `TurnEnded` and the next `TurnStarted`.

Required committed ordering:

```
... (normal turn events)
TurnEnded
CompactionCommitted
TurnStarted
... (next turn)
```

### Invariant: no open tool calls at TurnEnded
At the moment we commit `TurnEnded`, all tool calls requested during that turn must be in a terminal state.

This is enforced via a shared debug invariant (see `TurnInvariants.AssertNoOpenToolCallsAtTurnEnd`).

---

## Compaction input prompt

Compaction summarizes a range of committed events:

- Range start: after the last `CompactionCommitted` for the thread, or beginning of thread history.
- Range end: current committed position.

### What to include

- Preserve **system/user/assistant** messages from the event log.
- For tool calls:
  - include tool name
  - include call parameters (`args`)
  - include terminal status: `completed|failed|cancelled|rejected`
  - include short error reason for failures/rejections
  - **do not include** full tool output bodies

### Ordering

Compaction input must preserve the **chronological ordering** implied by the committed event log.

---

## Compaction model call

Compaction is a dedicated model call:

- Model: `CompactionModel` (friendly) resolved the same way as other model calls; defaults to `DefaultModel`.
- Tools: **disabled**.
- Prompt:
  - A special compaction system prompt requiring:
    - structured JSON with facts/decisions/problems/open questions/tool/action summaries
    - and a few paragraphs of prose summary
  - The compaction transcript payload is passed as a **user** message.

---

## Compaction events

Introduce:

- `ObservedCompactionGenerated` — observed result of compaction model call.
- `CompactionCommitted` — committed durable compaction artifact.

Suggested committed payload fields:

- `threadId`
- `fromEventIndex` / `toEventIndex` (or equivalent stable ids)
- `modelFriendly` and `providerModel`
- `structured` (JSON object)
- `proseSummary` (string)

---

## Prompt rendering after compaction

When rendering the next prompt for a thread:

1) If there is a `CompactionCommitted`, render its output as a **system message** at the top (after any always-injected system messages).
2) Append the last `CompactionTailMessageCount` messages verbatim.

### Special tail rule: tool results without assistant follow-up

If compaction occurred after tool calls/results but before the assistant produced a follow-up message, the tail must include those tool calls/results verbatim.

Rationale: without this, the next model call may not realize it is mid-plan and may repeat work or diverge.

Implementation sketch:
- Find the most recent committed assistant message in the post-compaction history.
- If there exist tool terminal events after that message (or no assistant message exists after those tool calls), include the corresponding tool call/result renderings in the tail.

---

## Test plan (TDD)

### 1) Trigger tests
- Known limit + `totalTokens` >= threshold => compaction scheduled.
- Unknown limit => no compaction.

### 2) Boundary ordering test
- When compaction due, assert committed sequence contains:
  - `TurnEnded`
  - `CompactionCommitted`
  - `TurnStarted`
  - and no other committed events between them.

### 3) Tool lifecycle + continuation test
- Model emits multiple tool calls, tools complete, then token usage crosses threshold.
- Assert:
  - the turn ends,
  - compaction occurs,
  - the next turn starts,
  - a follow-up model call is still made so the model can consume tool results.

### 4) Compaction input prompt shape
- Verify tool outputs are excluded; args + status included.
- Verify chronological ordering preserved.

### 5) Tail rendering rule
- If compaction occurs with tool results not yet followed by an assistant message, verify tail includes tool calls/results verbatim.

---

## Non-goals (for v1)

- Automatic discovery of context limits via provider APIs.
- Tool output inclusion in compaction transcript.
- Cross-thread/global compaction (each thread compacts independently).
