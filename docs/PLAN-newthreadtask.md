# Plan (TDD) — `NewThreadTask` / `thread_created` + fork read-window

## Quick implementation checklist (likely touch points)

**New event + inbox kind**
- `src/Agent.Harness/Threads/ThreadModels.cs` (add `ThreadInboxMessageKind.NewThreadTask` + meta fields)
- `src/Agent.Harness/SessionState.cs` (add `NewThreadTask` to committed event union)

**Promotion / reducer**
- `src/Agent.Harness/CoreReducer.cs` (map inbox → committed `NewThreadTask`)

**Prompt rendering**
- `src/Agent.Harness/Llm/MeaiPromptRenderer.cs` (render `<thread_created ... />`, optional `<notice>`, and `<task>`)

**Thread creation**
- `src/Agent.Harness/Threads/ThreadOrchestrator.cs` and/or ACP effect executor (ensure `thread_start` enqueues exactly one NewThreadTask; `isFork` derived from `context` parameter)

**Thread read filtering**
- `src/Agent.Harness/Acp/...` tool handler for `thread_read` (or wherever `thread_read` is implemented) — filter to fork-point for `IsFork==true`

**Tests (new)**
- `tests/Agent.Harness.Tests/CoreReducerNewThreadTaskPromotionTests.cs`
- `tests/Agent.Harness.Tests/MeaiPromptRendererNewThreadTaskTests.cs`
- `tests/Agent.Harness.Tests/ThreadStartEnqueuesNewThreadTaskTests.cs`
- `tests/Agent.Harness.Tests/ThreadReadForkWindowTests.cs`

## Goal
Introduce a first-class **NewThreadTask** concept so that:

## Progress (live)
- [x] Phase 1 — Domain types + reducer promotion
  - Added `ThreadInboxMessageKind.NewThreadTask`
  - Added committed event `NewThreadTask(ThreadId, ParentThreadId, IsFork, Message)`
  - Reducer promotes inbox kind → committed `NewThreadTask`
  - Tests: `CoreReducerNewThreadTaskPromotionTests`
- [x] Phase 2 — Prompt rendering
  - Renderer emits `<thread_created ... />`, optional `<notice>`, and `<task>...` for `NewThreadTask`
  - Tests: `MeaiPromptRendererNewThreadTaskTests`
- [ ] Phase 3 — `thread_start` enqueues `NewThreadTask`
- [ ] Phase 4 — `thread_read` fork window filtering
- [ ] Phase 5 — Integration/sample updates

1. Every newly created thread (including forks created via `thread_start` + `context`) receives **exactly one** `NewThreadTask` delivered via the inbox pipeline.
2. The task is **rendered into the model prompt** in a stable, explicit format that includes the **new thread id**, **parent id**, and the **task message**.
3. If the thread is a **fork**, `thread_read` for the parent (or anyone) returns only messages **from the fork point onward**, where the **fork point** is the committed `NewThreadTask` event.
4. No back-compat is required.

---

## Spec

### 1) New inbox kind
Add a new inbox message kind:

- `ThreadInboxMessageKind.NewThreadTask`

This is the *only* kind used for the “initial task” delivery. No inter-thread bootstrap message is used for this purpose.

### 2) New committed event
Add a new committed event type:

- `NewThreadTask(string ThreadId, string ParentThreadId, bool IsFork, string Message)`

Notes:
- Rename payload from `text` → `message`.
- Do **not** include parent intent.
- No optional fields.

### 3) Prompt rendering format
In `MeaiPromptRenderer.Render(SessionState state)`, render `NewThreadTask` as system messages in this exact shape:

```
<thread_created id="{threadId}" parent_id="{parentThreadId}" />
<notice>This is a forked thread with historical context that should be used when completing the task.</notice>
<task>{message}</task>
```

Rules:
- Always output the `<thread_created ... />` line.
- If `IsFork == true`, output the `<notice>...</notice>` line **before** `<task>`.
- Always output `<task>...</task>`.
- Escaping: keep it simple/consistent with existing renderer conventions (string interpolation); do not attempt to HTML-escape unless you already do for other tags.

### 4) Fork read-window
If a thread is forked (`IsFork == true`), then `thread_read(threadId)` should only return messages **from the fork point onward**.

Fork point definition (per spec choice “A”):
- The first committed `NewThreadTask` event in that thread.

Filtering rule:
- When `IsFork == true`, `thread_read` returns only messages/events at-or-after that event.
- Include the task message in the returned list (see below).

### 5) `thread_read` output shape
`thread_read` returns a list of messages with role + message text (existing shape).

Decision for `NewThreadTask` in read output:
- Include it as a **system** message formatted the same way as prompt rendering (or a simplified variant), so the parent can understand what the fork’s task was.

---

## Implementation plan (TDD-first)

### Phase 1 — Domain types + reducer promotion

#### 1.1 Add types
- Add `ThreadInboxMessageKind.NewThreadTask`.
- Add committed event `NewThreadTask`.

#### 1.2 Reducer: promote inbox → committed
Wherever you currently promote inbox items (immediate/enqueue) into committed events:
- Add a new branch that maps `ThreadInboxMessageEnqueued(kind=NewThreadTask, meta={parent,isFork}, message)` into committed `NewThreadTask`.

✅ **Test first**
Create `tests/Agent.Harness.Tests/CoreReducerNewThreadTaskPromotionTests.cs`:
- Arrange: state with a committed `ThreadInboxMessageEnqueued` of kind `NewThreadTask`.
- Act: reduce/promotion step.
- Assert: committed includes exactly one `NewThreadTask` with correct fields.

### Phase 2 — Prompt rendering

#### 2.1 Renderer changes
Update `src/Agent.Harness/Llm/MeaiPromptRenderer.cs`:
- Add `case NewThreadTask nt:` → emits system message(s) exactly per format.

✅ **Test first**
Create `tests/Agent.Harness.Tests/MeaiPromptRendererNewThreadTaskTests.cs`:
- Non-fork case: contains `<thread_created .../>` + `<task>...` and **does not** contain `<notice>`.
- Fork case: contains `<thread_created .../>`, then `<notice>...`, then `<task>...`.

### Phase 3 — `thread_start` always enqueues a `NewThreadTask`

#### 3.1 Define the creation API inputs
Per clarification: `thread_start` supports forking via a `context` parameter.
- If `context` provided → `IsFork=true`.
- Else → `IsFork=false`.

#### 3.2 Creation pipeline
When a thread is created:
- Ensure the new thread id is known.
- Enqueue exactly one inbox item into the *child* thread:
  - kind: `NewThreadTask`
  - meta: `{ parentThreadId, isFork }`
  - message: the task message argument supplied to `thread_start` (whatever you currently call it)

Important invariant:
- Do not append committed events directly; go through observed inbox enqueue → reducer.

✅ **Test first (preferred: deterministic orchestrator boundary)**
Create `tests/Agent.Harness.Tests/ThreadStartEnqueuesNewThreadTaskTests.cs`:
- Use `InMemoryThreadStore`.
- Execute the thread creation pathway (via tool router / orchestrator seam used by existing tests).
- Assert in the child thread log:
  - exactly one `ThreadInboxMessageEnqueued(kind=NewThreadTask, ...)` OR after promotion exactly one committed `NewThreadTask`.
- Assert no extra `InterThreadMessage` bootstrap is created.

### Phase 4 — `thread_read` fork window filtering

#### 4.1 Implement fork boundary discovery
In the thread-read query path:
- Load committed events for the thread.
- Locate first `NewThreadTask`.
- If it’s fork (`IsFork==true`), discard prior events from the returned message list.

✅ **Test first**
Create `tests/Agent.Harness.Tests/ThreadReadForkWindowTests.cs`:
- Arrange child thread committed events:
  - (some earlier history)
  - `NewThreadTask(IsFork=true, ...)`
  - later messages
- Act: `thread_read`.
- Assert: returned list starts with the system message corresponding to `NewThreadTask` and does not include earlier history.

### Phase 5 — Update/extend integration coverage

✅ Optional integration test
If you already have an integration test around `thread_start` + `thread_read`, extend it to cover:
- Forked thread read-window behavior
- Prompt rendering includes the new tag(s)

---

## Acceptance criteria
- [ ] Every new thread created via `thread_start` receives exactly one `NewThreadTask`.
- [ ] `NewThreadTask` is delivered through inbox → reducer (no direct committed appends).
- [ ] Prompt contains `<thread_created id="..." parent_id="..." />` and `<task>...</task>`; fork includes `<notice>...</notice>`.
- [ ] `thread_read` for forked threads returns messages only from the `NewThreadTask` marker onward.
- [ ] Non-fork threads are not filtered (unless you explicitly choose to).
- [ ] Release test suite remains green.

---

## Notes / known sharp edges
- “Multiple model invocations per single `acpx prompt`” is expected; the new tags make it easier for the model to reason about thread provenance.
- No back-compat: older sessions without `NewThreadTask` can simply behave as-is (or tests can assume new sessions only).
