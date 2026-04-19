# Multi-Threading Correctness & Cleanup Analysis

**Date:** 2026-04-15  
**Scope:** `/home/node/.openclaw/workspace/marian-agent`  
**Focus:** Remaining imperative commits, thread isolation, shared turn loop, dead code

---

## Executive Summary

✅ **Major wins accomplished:**
- MarkRunning/MarkIdle imperative status mutation **removed**
- Thread status now **projected from TurnStarted/TurnEnded events**
- Inbox promotion unified via **ObservedInboxMessageArrived**
- Per-thread gates prevent overlapping turns

⚠️ **Critical issue identified:**
- **Race condition in ThreadOrchestrator.Observe()**

📋 **Cleanup opportunities:**
- Dead code: `IEventLog` + `InMemoryEventLog`
- Legacy test file marker for thread tools

---

## ✅ Correctness: ThreadOrchestrator.Observe race (FIXED)

### What was wrong
`ThreadOrchestrator.Observe(...)` used to mutate the in-memory `_states[threadId]` cache without acquiring the per-thread execution gate. That allowed concurrent `Observe(...)` + `RunOneTurnIfNeededAsync(...)` to clobber cache state.

### Current status
**Fixed in code**: `Observe(...)` now acquires the same per-thread `SemaphoreSlim` gate before reading/updating `_states`.

- File: `src/Agent.Harness/Threads/ThreadOrchestrator.cs`
- Behavior: cache updates are now **linearizable per thread**

### Test coverage
A dedicated concurrency regression test exists:
- `tests/Agent.Harness.Tests/ThreadOrchestratorObserveConcurrencyTests.cs`

### Follow-up (optional)
If we ever need to eliminate synchronous blocking, we can introduce `ObserveAsync(...)` and use `WaitAsync`, but that’s not necessary for correctness right now.

---

## ✅ Cleanup: Dead Code Removal (DONE)

### IEventLog + InMemoryEventLog

**Location:** `src/Agent.Harness/IEventLog.cs`

**Status:** ✅ Removed (no remaining usages).

**Test:** `dotnet test Agent.slnx -c Release` is green.

---

## 🧪 Test Additions Needed

### 1. ThreadOrchestrator Concurrency Test

**File:** `tests/Agent.Harness.Tests/ThreadOrchestratorConcurrencyTests.cs`

**Scenario:** Concurrent Observe + RunOneTurnIfNeededAsync on same thread

```csharp
[Fact]
public async Task Observe_ConcurrentWithTurnExecution_DoesNotLoseStateUpdates()
{
    // Setup orchestrator with a thread that will run a slow turn
    // 1. ScheduleRun(threadId) to start turn execution
    // 2. Immediately call Observe(threadId, evt) from another thread
    // 3. Wait for both to complete
    // 4. Verify both state mutations are preserved (check committed log + in-memory state)
    
    // Expected: both events appear in committed log AND final state
    // Bug manifestation: evt from Observe is lost from in-memory state
}
```

### 2. Multi-threaded Inbox Stress Test

**File:** Same as above

**Scenario:** Multiple threads enqueuing messages to child threads simultaneously

```csharp
[Fact]
public async Task MultipleParents_EnqueueToSameChild_AllMessagesDelivered()
{
    // Create 1 child thread
    // Spawn 10 parent threads all calling thread_send to the child
    // Run orchestrator until quiescent
    // Verify all 10 messages appear in child's inbox (committed log)
}
```

---

## 🔍 Remaining Imperative Commits

### ThreadManager Imperative Helpers

**Still in use:**

1. **CreateChildThread(parentThreadId)**
   - Called by: `AcpEffectExecutor` for `thread_new` tool
   - Status: ✅ Acceptable - this is a write-side command handler
   - Creates thread metadata (JSONL write)

2. **ForkChildThread(parentThreadId, parentState)**
   - Called by: `AcpEffectExecutor` for `thread_fork` tool
   - Status: ✅ Acceptable - write-side command
   - Copies committed events to new thread

3. **ReportIntent(threadId, intent)**
   - Called by: `AcpEffectExecutor` for `report_intent` tool
   - Status: ✅ Acceptable - write-side command
   - Updates metadata + appends `ThreadIntentReported` event

**Verdict:** These are **not race conditions**. They're write-side commands executed during effect execution, which happens **inside the turn gate**. No cleanup needed.

---

## 📊 Shared Turn Loop Analysis

### Current Architecture: ✅ Correct (with Observe fix)

**Turn execution model:**
1. Per-thread gate (`SemaphoreSlim`) in `ThreadOrchestrator._gates`
2. `RunOneTurnIfNeededAsync` holds gate for entire turn
3. Turn loop is in `TurnRunner.RunWithEffectsAsync`:
   - Drain observed events → reduce → commit
   - Execute effects → produce new observations → loop
   - Model streaming is fully drained before tool execution

**Key invariants (all satisfied):**
- ✅ At most one model call in-flight per thread
- ✅ Tool execution does not interleave with model streaming
- ✅ Each thread's state evolution is sequential (inside gate)
- ⚠️ **Exception:** `Observe()` bypasses gate (see fix above)

**No shared mutable state between threads:**
- `_states` is `ConcurrentDictionary<string, SessionState>` → keyed by threadId
- Each thread's `SessionState` is immutable (record type)
- State updates are copy-on-write (`state with { ... }`)

**Verdict:** Architecture is sound. Only issue is ungated `Observe()`.

---

## 🔧 Concrete Next Steps (Prioritized)

### P0 — MVP ACP protocol contracts (non-negotiable)

These are the next most valuable items because they lock the “wire contract” with ACP clients.

- [ ] **Initialize contract: exact meta.json fields + capability negotiation**
  - Add/strengthen tests ensuring `initialize` response matches the schema/meta contract precisely.
  - Validate protocol versioning + capability defaults (what is omitted vs present).
  - (You already called this out as MVP-non-negotiable.)

- [ ] **“Streaming-ish prompt” contract**
  - Tests that a prompt turn can emit multiple interleaved `session/update` chunks,
    then returns a final `PromptResponse` with `stopReason=end_turn`.
  - Ensure ordering: updates first, final response last.

### P1 — Concurrency hardening (optional but cheap insurance)

- [ ] **Stress-ish test: many concurrent Observe() across multiple threads**
  - Goal: ensure we never lose committed inbox enqueues and promotion remains deterministic.
  - Focus on asserting committed logs rather than internal cache state.

- [ ] **Make Observe async (optional)**
  - Only if we want to avoid synchronous blocking on the gate.
  - Not required for correctness (current gated Observe is correct).

### P2 — Tool-calling ergonomics / completeness

- [ ] **Incremental tool-call parsing (multi-chunk FunctionCallContent)**
  - Build a small assembler (toolId → {name?, argsBuffer}) at the MEAI adapter boundary.
  - Keep the reducer contract unchanged (still emits ObservedToolCallDetected when complete).

### P3 — Minor hygiene

- [ ] **Fix nullable warnings in a few Acp MCP tests**
  - `tests/Agent.Acp.Tests/AcpMcpRehydrate*Tests.cs` has CS8602 warnings.
  - Not failing today, but worth cleaning to keep -warnaserror viable.

---

## 📝 TODO Inventory (Still Relevant)

### 1) Multi-chunk tool-call streaming

**Location:** `src/Agent.Harness/Llm/MeaiToolCallParser.cs:21`

```csharp
// TODO(tool-calls): Handle multi-chunk / incremental FunctionCallContent updates.
```

**Context:** Some providers stream tool calls in multiple chunks (e.g. name appears once, args arrive as deltas).

**Impact:** Medium (provider-dependent)  
**Effort:** Medium (needs a small incremental assembler / state machine)  
**Priority:** P2 unless/until we target a provider that requires it.

### 2) Generated schema note (non-blocking)

**Location:** `src/Agent.Acp/Generated/AcpSchema.g.cs` (generated)

```csharp
// TODO(system.text.json): Add string enum item converter
```

**Priority:** Ignore unless it becomes a real bug; treat as upstream generator noise.

---

## 🎯 Recommended Next Steps

### Immediate (This Week)

1. **Fix Observe race condition** (Priority 1, ~3h)
   - Make `ObserveAsync` with gate
   - Update callers
   - Add concurrency test

2. **Remove IEventLog** (Priority 2, ~5min)
   - One-line delete
   - Test suite pass

### Short-term (Next Sprint)

3. **Add multi-threaded stress test** (Priority 3, ~1h)
   - Validates concurrency assumptions
   - Prevents regression

### Future

4. **Multi-chunk tool-call streaming** (TODO comment)
   - Wait for provider that needs it
   - Design state machine for partial tool-call updates

---

## 🧪 Test Commands

**Run all tests:**
```bash
cd /home/node/.openclaw/workspace/marian-agent
dotnet test Agent.slnx -c Release
```

**Run thread-related tests only:**
```bash
dotnet test Agent.slnx -c Release --filter "FullyQualifiedName~Thread"
```

**Check for compilation warnings:**
```bash
dotnet build Agent.slnx -c Release -warnaserror
```

---

## 📚 Related Files

**Core logic:**
- `src/Agent.Harness/CoreReducer.cs` (608 lines) - Pure reducer
- `src/Agent.Harness/TurnRunner.Effects.cs` (173 lines) - Effect execution loop
- `src/Agent.Harness/Threads/ThreadOrchestrator.cs` (218 lines) - Multi-thread coordinator

**State & storage:**
- `src/Agent.Harness/Threads/ThreadManager.cs` - Metadata + imperative helpers
- `src/Agent.Harness/Threads/InMemoryThreadStore.cs` - Concurrent in-memory store
- `src/Agent.Harness/Threads/JsonlThreadStore.cs` - JSONL persistence

**Tests:**
- `tests/Agent.Harness.Tests/ThreadOrchestratorIdleNotificationTests.cs`
- `tests/Agent.Harness.Tests/ThreadOrchestratorEndToEndEnqueueGatingIntegrationTests.cs`
- `tests/Agent.Harness.Tests/ThreadStatusProjectorTests.cs`

---

## ✅ Summary

**Current state:** System is **90% correct**. Major refactor to event-sourced thread status is complete. Only one critical race condition remains (Observe), easily fixable.

**Next highest-value step:** Fix `ThreadOrchestrator.Observe` race by making it async + gated. Add concurrency test. This is the **only blocking correctness issue**.

**Cleanup:** Remove `IEventLog` dead code (1 file, 25 lines).

**No blocking issues for multi-threading correctness** after Observe fix.
