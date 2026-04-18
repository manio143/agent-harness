# 🚧 PENDING - Unify thread runner / sink-only persistence

## Goal
Unify main-thread vs child-thread execution so that the *only* difference is sink decoration (ACP publishing/listeners on main). Eliminate duplicated responsibilities that caused bugs (e.g., session title event not persisted).

## What's actually done so far ✅
- **ObserveAsync** no longer persists committed events directly; it queues observations and schedules a wake-driven turn.
- **Idle notifications** (child → parent) now flow via ObserveAsync → reducer → sink (no direct store append from orchestrator).
- **ForkChildThread** routes forked events through `ThreadEventSink.OnCommittedAsync` (no direct store append).
- **All orchestrator logic** routes commits via sinks (verified: no `.AppendCommittedEvent` calls outside of sink implementations).
- Harness + ACP test suites are green:
  - Agent.Acp.Tests: 74 passed
  - Agent.Harness.Tests: 142 passed

## MVP / non-negotiable behaviors (tests exist) ✅
These contract tests already exist and are passing:
- **ACP initialize/meta.json**: `AcpInitializeMetaJsonContractTests`, `AcpInitializeContractTests`, capability negotiation tests.
- **"Streaming-ish prompt" semantics**: `AcpSessionUpdateStreamingContractTests` (multiple `session/update` before final response).
- **No re-entrancy invariant**: `ThreadSendSelfEnqueueDoesNotDeadlockIntegrationTests`.

## Target invariants ✅
- ✅ **All committed events must go through `IEventSink.OnCommittedAsync`** (no direct store writes from orchestrator logic).
  - Verified: No `.AppendCommittedEvent` calls exist outside of sink implementations (`MainThreadEventSink`, `ThreadEventSink`).
  - `ForkChildThread` creates a `ThreadEventSink` and routes forked events through `OnCommittedAsync`.
  - `ThreadOrchestratorObservePersistsViaSinkTests` validates this invariant.
- ✅ **Thread scheduling/execution is unified for main + child** (only sink decoration differs).
  - Both use `RunOneTurnAsync` → `SessionRunner.RunTurnAsync` with sink parameter.
  - Main gets `MainThreadEventSink` (persists + projects ACP), child gets `ThreadEventSink` (persists only).
- ✅ **At most one model call in-flight per thread** (hard invariant).
  - Per-thread `SemaphoreSlim` gate in `ThreadOrchestrator._gates` enforces this.
  - `ThreadSendSelfEnqueueDoesNotDeadlockIntegrationTests` validates no re-entrancy deadlock.

## Notes
- Contract tests exist and pass:
  - `ThreadOrchestratorObservePersistsViaSinkTests` - validates sink-only persistence
  - `ThreadOrchestratorObserveConcurrencyTests` - validates concurrency safety
  - `AcpInitializeMetaJsonContractTests` - validates ACP initialize/meta.json protocol
  - `AcpSessionUpdateStreamingContractTests` - validates streaming session/update semantics
  - `ThreadSendSelfEnqueueDoesNotDeadlockIntegrationTests` - validates no re-entrancy deadlock

## Next refactor (unification) — single orchestrator owns thread lifecycle
We want a single `ThreadOrchestrator` to own the thread universe: creation/forking, scheduling, and execution.
Effects (and other imperative shells) should request thread operations by emitting **observed events**, which flow back into the orchestrator.

TDD work items:
- [ ] Add an observed event for thread fork/create requests.
- [ ] Add an integration test proving: fork request → child thread created → parent history seeded.
- [ ] Move any remaining thread lifecycle responsibilities out of `ThreadManager` (Option A: delete, or Option B: keep as projector-only).

## Status: 🚧 IN PROGRESS
