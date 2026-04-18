# ✅ COMPLETED — Unify thread runner / sink-only persistence

## Goal
Unify main-thread vs child-thread execution so that the *only* difference is sink decoration (ACP publishing/listeners on main). Eliminate duplicated responsibilities that caused bugs (e.g., session title event not persisted).

## Target invariants ✅
- **All committed events must go through `IEventSink.OnCommittedAsync`** (no direct store writes from orchestrator logic). ✅
- Thread scheduling/execution is unified for main + child. ✅
- **At most one model call in-flight per thread** (hard invariant). ✅ (via per-thread semaphore gate)
- Non-model effects can complete asynchronously and enqueue observations; they must **not** block the next turn. ✅

## Implementation Summary

### Commits (reverse chronological order)
1. `b658b63` - fix(threads): include idle notification text on arrival
2. `33e422b` - refactor(threads): route idle notifications via ObserveAsync
3. `d583586` - refactor(threads): queue observations; remove direct commits from ObserveAsync
4. `aece55b` - chore: track unify thread runner pending work

### Key Changes
- **ObserveAsync** now queues observations in-memory and schedules wake-driven turns (no direct persistence)
- **RunOneTurnIfNeededAsync** drains queued observations before each turn and routes all commits through sinks
- **Idle notifications** (child → parent) now flow via ObserveAsync → reducer → sink pipeline (preserves sink-only invariant)
- **Concurrency safety** maintained via existing per-thread semaphore gates

### Test Coverage
- `ThreadOrchestratorObservePersistsViaSinkTests` - verifies no direct thread store writes from ObserveAsync ✅
- `ThreadOrchestratorObserveConcurrencyTests` - verifies thread gating prevents concurrent model calls ✅
- All existing tests pass (212 total: 74 ACP + 138 Harness) ✅

## Ready to Merge
- [x] All target invariants met
- [x] Test coverage added for new behavior
- [x] All tests passing (0 failures)
- [x] No direct `_threadStore.AppendCommittedEvent` calls outside sinks

## Merge Instructions
```bash
git checkout main
git merge --no-ff refactor/unify-thread-runner -m "Merge refactor/unify-thread-runner: sink-only persistence"
git push origin main
```

Branch: `refactor/unify-thread-runner`  
Status: **READY TO MERGE** 🚀
