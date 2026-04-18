# 🚧 PENDING — Unify thread runner / sink-only persistence

## Goal
Unify main-thread vs child-thread execution so that the *only* difference is sink decoration (ACP publishing/listeners on main). Eliminate duplicated responsibilities that caused bugs (e.g., session title event not persisted).

## What’s actually done so far ✅
- **ObserveAsync** no longer persists committed events directly; it queues observations and schedules a wake-driven turn.
- **Idle notifications** (child → parent) now flow via ObserveAsync → reducer → sink (no direct store append from orchestrator).
- Test suites are green locally:
  - Agent.Acp.Tests: 74 passed
  - Agent.Harness.Tests: 138 passed

## Still NOT done (do not merge yet) ❌
MVP / non‑negotiable behaviors we still need tests for (and implementations if missing):
- **ACP initialize/meta.json exact fields**: protocol version + capability negotiation must match schema.
- **“Streaming‑ish prompt” semantics**: multiple `session/update` chunks interleaved, then final `PromptResponse.stopReason=end_turn`.
- **No re‑entrancy invariant**: self‑send enqueue during a turn must not deadlock.

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
