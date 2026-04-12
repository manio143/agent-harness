# Harness SessionRunner (State Ownership + Post-Turn Policies)

## Problem

We want strict separation between:

- **Harness domain semantics**: how a session evolves over time, which events are committed, and which post-turn policies apply.
- **Server / adapter concerns**: transport (ACP), configuration, persistence directory selection, and publishing committed events.

A smell emerged when implementing session title generation:

- After a prompt turn finished, the **server** generated a `SessionTitleSet` committed event and then manually mutated `SessionState` (`Committed = Committed.Add(evt)`).

Even though `SessionState` is an immutable value, the server becoming responsible for producing and applying a domain event is an architectural leak:

- The server is now partially responsible for session evolution.
- Post-turn policies would proliferate in the server (titles today, summaries tomorrow, etc.).
- Tests become harder: harness unit tests can't fully describe session evolution if the server makes domain decisions.

## Decision

Introduce a harness-owned imperative-shell orchestrator: **`SessionRunner`**.

`SessionRunner` owns the session lifecycle for a single turn:

1. Consume observed events.
2. Reduce them through the core reducer (commit gate) to produce committed events.
3. Apply post-turn policies (e.g., title generation) and commit any resulting domain events.
4. Return:
   - the next `SessionState`
   - the full set of newly-committed events

## Consequences

- The server no longer applies domain events to state.
- The server only:
  - provides observed streams
  - persists committed events (append-only)
  - publishes committed events over ACP
  - replaces its local state with `TurnResult.Next`

This keeps the functional core pure, keeps post-turn policies testable at the harness level, and makes new policies easy to add without infecting transports/adapters.

## Notes

- `SessionRunner` is still part of the harness imperative shell (it can do I/O via injected interfaces like `IChatClient`), but it is **harness-owned**, not adapter-owned.
- Persisted metadata remains a projection of committed events.
