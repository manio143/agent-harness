# 🚧 PENDING — Unify thread runner / sink-only persistence

## Goal
Unify main-thread vs child-thread execution so that the *only* difference is sink decoration (ACP publishing/listeners on main). Eliminate duplicated responsibilities that caused bugs (e.g., session title event not persisted).

## What’s actually done so far ✅
- **ObserveAsync** no longer persists committed events directly; it queues observations and schedules a wake-driven turn.
- **Idle notifications** (child → parent) now flow via ObserveAsync → reducer → sink (no direct store append from orchestrator).
- Harness + ACP test suites are green locally:
  - Agent.Acp.Tests: 74 passed
  - Agent.Harness.Tests: 138 passed

## MVP / non‑negotiable behaviors (tests exist) ✅
These contract tests already exist and are passing:
- **ACP initialize/meta.json**: `AcpInitializeMetaJsonContractTests`, `AcpInitializeContractTests`, capability negotiation tests.
- **“Streaming‑ish prompt” semantics**: `AcpSessionUpdateStreamingContractTests` (multiple `session/update` before final response).
- **No re‑entrancy invariant**: `ThreadSendSelfEnqueueDoesNotDeadlockIntegrationTests`.

## Target invariants (work in progress)
- [ ] All committed events must go through `IEventSink.OnCommittedAsync` (no direct store writes from orchestrator logic).
- [ ] Thread scheduling/execution is unified for main + child (only sink decoration differs).
- [ ] At most one model call in-flight per thread (hard invariant).

## Notes
- We have early coverage tests:
  - `ThreadOrchestratorObservePersistsViaSinkTests`
  - `ThreadOrchestratorObserveConcurrencyTests`
- These do **not** yet prove the MVP ACP protocol behaviors above.

## Next step
Pick one non‑negotiable above and drive it with a failing integration test first, then implement.

Branch: `refactor/unify-thread-runner`
Status: **NOT READY TO MERGE YET**
