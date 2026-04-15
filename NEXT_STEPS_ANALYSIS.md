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

## 🚨 Critical: ThreadOrchestrator State Race Condition

### Location
`src/Agent.Harness/Threads/ThreadOrchestrator.cs` lines 96-119

### Problem
```csharp
public void Observe(string threadId, ObservedChatEvent observed)
{
    var initial = _states.GetOrAdd(threadId, _ => SessionState.Empty with { ... });
    
    var reduced = Core.Reduce(initial, observed);
    
    foreach (var evt in reduced.NewlyCommitted)
        _threadStore.AppendCommittedEvent(_sessionId, threadId, evt);
    
    _states[threadId] = reduced.Next;  // ⚠️ RACE HERE!
    
    if (reduced.Effects.Any(e => e is CallModel))
        ScheduleRun(threadId);
}
```

**Race scenario:**
1. Thread A calls `Observe(thr_x, evt1)` → reads `_states[thr_x]` = S₀
2. Thread B calls `RunOneTurnIfNeededAsync(thr_x)` → reads `_states[thr_x]` = S₀ (gate held)
3. Thread A reduces evt1 → writes `_states[thr_x]` = S₁
4. Thread B completes turn → writes `_states[thr_x]` = S₂ (derived from S₀, not S₁)
5. **Result:** evt1's state update is lost!

### Impact
- **Lost events** when Observe() is called concurrently with turn execution
- Events still persisted to disk (AppendCommittedEvent), but in-memory state diverges
- Subsequent turns may see stale state

### Fix Options

#### Option A: Extend gate to Observe (recommended)
```csharp
public void Observe(string threadId, ObservedChatEvent observed)
{
    var gate = _gates.GetOrAdd(threadId, _ => new SemaphoreSlim(1, 1));
    gate.Wait();  // or make Observe async with WaitAsync
    try
    {
        var initial = _states.GetOrAdd(threadId, _ => SessionState.Empty with { ... });
        var reduced = Core.Reduce(initial, observed);
        
        foreach (var evt in reduced.NewlyCommitted)
            _threadStore.AppendCommittedEvent(_sessionId, threadId, evt);
        
        _states[threadId] = reduced.Next;
        
        if (reduced.Effects.Any(e => e is CallModel))
            ScheduleRun(threadId);
    }
    finally
    {
        gate.Release();
    }
}
```

**Pros:**
- Simple, consistent with existing pattern
- Guarantees sequential state evolution per thread

**Cons:**
- Makes `Observe()` synchronous-wait on gate (blocking)
- Better to make it async: `Task ObserveAsync(...)`

#### Option B: Rebuild state from committed log (functional purity)
```csharp
public void Observe(string threadId, ObservedChatEvent observed)
{
    // Don't cache state at all; always rebuild from committed events.
    var committed = _threadStore.LoadCommittedEvents(_sessionId, threadId);
    var initial = ReplayCommittedEvents(committed);  // pure rebuild
    
    var reduced = Core.Reduce(initial, observed);
    
    var gate = _gates.GetOrAdd(threadId, _ => new SemaphoreSlim(1, 1));
    gate.Wait();
    try
    {
        foreach (var evt in reduced.NewlyCommitted)
            _threadStore.AppendCommittedEvent(_sessionId, threadId, evt);
    }
    finally
    {
        gate.Release();
    }
    
    // No in-memory state caching; read it back on-demand.
    if (reduced.Effects.Any(e => e is CallModel))
        ScheduleRun(threadId);
}
```

**Pros:**
- Functionally pure: state is always derived from log (single source of truth)
- Eliminates state caching race entirely

**Cons:**
- Performance: replaying N events on every Observe call
- For long threads (100s of turns), this could be expensive

#### Option C: Hybrid - CoW (Copy-on-Write) state updates
Use `ImmutableInterlocked.Update` pattern:
```csharp
ImmutableInterlocked.Update(ref _states, (dict, ctx) => 
{
    var (threadId, observed) = ctx;
    var initial = dict.GetValueOrDefault(threadId, SessionState.Empty with { ... });
    var reduced = Core.Reduce(initial, observed);
    return dict.SetItem(threadId, reduced.Next);
}, (threadId, observed));
```

**Pros:**
- Lock-free, wait-free
- Preserves in-memory cache performance

**Cons:**
- More complex
- Still need to gate `AppendCommittedEvent` writes

### Recommendation
**Go with Option A (extend gate), but make Observe async.**

Change signature:
```csharp
public async Task ObserveAsync(string threadId, ObservedChatEvent observed, CancellationToken ct = default)
```

Callers become:
```csharp
await orchestrator.ObserveAsync(threadId, evt, cancellationToken);
```

This is the **smallest change** with **clearest correctness guarantees**.

---

## 🧹 Dead Code Removal

### 1. IEventLog + InMemoryEventLog

**Location:** `src/Agent.Harness/IEventLog.cs`

**Status:** ✅ Confirmed dead
- Only referenced in its own file
- No usages in `src/` or `tests/`
- Comment says "Legacy mutable log interface (will likely be removed)"

**Action:**
```bash
rm src/Agent.Harness/IEventLog.cs
```

**Test:** Run full test suite to confirm no hidden dependencies:
```bash
dotnet test MarianAgent.slnx -c Release
```

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

## 🔧 Concrete TODOs (Prioritized)

### Priority 1: Correctness (Must-Have)

- [ ] **Fix ThreadOrchestrator.Observe race condition**
  - Make `Observe` async: `Task ObserveAsync(...)`
  - Acquire per-thread gate before state mutation
  - Update all callers (AcpEffectExecutor, tests)
  - **Estimated effort:** 2-3 hours
  - **Risk:** Low (mechanical refactor)

- [ ] **Add concurrency test for Observe**
  - `ThreadOrchestratorConcurrencyTests.cs`
  - Concurrent Observe + turn execution
  - Verify no lost state updates
  - **Estimated effort:** 1-2 hours

### Priority 2: Cleanup (Nice-to-Have)

- [ ] **Remove IEventLog dead code**
  - Delete `src/Agent.Harness/IEventLog.cs`
  - Run full test suite to verify
  - **Estimated effort:** 5 minutes
  - **Risk:** Near-zero

- [ ] **Rename legacy test file**
  - `AcpEffectExecutorThreadToolLegacyPathTests.cs` → still valid!
  - Actually tests that legacy path is **removed** (throws instead of using old API)
  - **Action:** No rename needed; update comment to clarify intent
  - **Estimated effort:** 2 minutes

### Priority 3: Hardening (Optional)

- [ ] **Add multi-threaded inbox stress test**
  - Multiple threads enqueuing to same child
  - Verify all messages delivered + processed
  - **Estimated effort:** 1 hour

- [ ] **Consider making ThreadManager methods async**
  - `CreateChildThread`, `ForkChildThread`, `ReportIntent`
  - Would allow async file I/O in JsonlThreadStore
  - **Estimated effort:** 2-3 hours
  - **Benefit:** Low (current sync I/O is fine for JSONL appends)

---

## 📝 One TODO Comment Found

**Location:** `src/Agent.Harness/Llm/MeaiToolCallParser.cs:21`

```csharp
// TODO(tool-calls): Handle multi-chunk / incremental FunctionCallContent updates.
```

**Context:** Some LLM providers stream tool calls in multiple chunks (args arriving incrementally). Currently treats each `FunctionCallContent` as complete.

**Impact:** Low (works for current providers)  
**Effort:** Medium (requires streaming tool-call state machine)  
**Priority:** Defer until multi-chunk provider is added

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
dotnet test MarianAgent.slnx -c Release
```

**Run thread-related tests only:**
```bash
dotnet test MarianAgent.slnx -c Release --filter "FullyQualifiedName~Thread"
```

**Check for compilation warnings:**
```bash
dotnet build MarianAgent.slnx -c Release -warnaserror
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
