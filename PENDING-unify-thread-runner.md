# Pending Work — Unify thread runner / sink-only persistence

## Goal
Unify main-thread vs child-thread execution so that the *only* difference is sink decoration (ACP publishing/listeners on main). Eliminate duplicated responsibilities that caused bugs (e.g., session title event not persisted).

## Target invariants
- **All committed events must go through `IEventSink.OnCommittedAsync`** (no direct store writes from orchestrator logic).
- Thread scheduling/execution is unified for main + child.
- **At most one model call in-flight per thread** (hard invariant).
- Non-model effects can complete asynchronously and enqueue observations; they must **not** block the next turn.

## Plan (TDD)
1. Add integration tests that fail on current behavior:
   - `ObserveAsync`-style intake commits are persisted via sink (no direct `_threadStore.AppendCommittedEvent` outside sinks).
   - Main thread metadata projection (`updatedAtIso`) occurs consistently for committed events that go through the persistence sink.
   - `modelInFlight` gating: ensure no concurrent model calls per thread even when multiple wakes happen.
2. Refactor:
   - Introduce a single persistence sink implementation that persists committed events to thread store and optionally projects session metadata when `threadId == main`.
   - Route all committed events (including those produced by `ThreadOrchestrator.ObserveAsync`) through sink(s).
   - Remove direct calls to `_threadStore.AppendCommittedEvent` from orchestrator logic.
3. Keep behavior:
   - ACP publishing remains only for main thread via sink wrapper.
   - Child threads use persistence-only sink.

## Rollback strategy
If tests explode or behavior regresses, reset branch to `origin/main` and re-scope.

## Notes
- Branch: `refactor/unify-thread-runner`
- Merge policy: merge back to `main` with a **merge commit** (no fast-forward).
