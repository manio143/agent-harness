# 🚧 PENDING - Unify thread runner / sink-only persistence

## Goal
Unify main-thread vs child-thread execution so that the *only* difference is sink decoration (ACP publishing/listeners on main). Eliminate duplicated responsibilities that caused bugs (e.g., session title event not persisted).

## What's actually done so far ✅
- **ObserveAsync** no longer persists committed events directly; it queues observations and schedules a wake-driven turn.
- **Idle notifications** (child → parent) flow via ObserveAsync → reducer → sink (no direct store append from orchestrator).
- **Fork lifecycle is API-only**: `ThreadOrchestrator.RequestForkChildThreadAsync(...)` (no lifecycle-as-observed-event).
- **Single orchestrator loop** runs main + child threads; **only sink decoration differs**:
  - main thread uses `MainThreadEventSink` + ACP projection
  - child threads use `ThreadEventSink`
- Harness + ACP test suites are green (Release):
  - Agent.Acp.Tests: 74 passed
  - Agent.Harness.Tests: 154 passed

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

## Next refactor / cleanup targets
Now that the two-lane model is in place (observations via `ObserveAsync`, lifecycle via explicit APIs), remaining work is mostly cleanup and hardening:

- [ ] Remove stale comments/docs/tests that still describe lifecycle-as-observation.
- [ ] Consider further interface narrowing: ensure `AcpEffectExecutor` depends only on the minimal thread interfaces (`IThreadObserver`, `IThreadLifecycle`, `IThreadScheduler`, `IThreadTools`).
- [ ] Consider whether `_states` cache in `ThreadOrchestrator` is still needed or can be reduced/removed (since committed state is reloaded from store per wake).
- [ ] Add/adjust integration tests where they meaningfully reduce future regressions (esp. around tool catalog changes + quiescence scheduling).

## Status: 🚧 IN PROGRESS
