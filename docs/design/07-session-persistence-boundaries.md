# Session Persistence Boundaries (Harness vs Server)

## Goals

- Persist **committed** session events (append-only) in a stable format.
- Allow resuming a previous session by replaying committed events into `SessionState`.
- Keep ACP (transport/protocol) as an adapter, not a policy owner.

## Boundary

### Agent.Harness owns

- The domain model for session history:
  - `SessionEvent` (committed) types (e.g. `UserMessage`, `AssistantMessage`, `AssistantTextDelta`, `ReasoningTextDelta`)
  - `SessionState` and reducer semantics
- Persistence interfaces (to be introduced):
  - session store abstraction (create/load/list/exists)
  - append-only writer for committed events
- Serialization format decisions for committed events (JSONL), including:
  - stable `type` discriminators
  - forward-compat rules (unknown events ignored)
- Metadata schema for sessions (e.g. `session.json`), if/when added

### Agent.Server owns

- Configuration: where persisted data lives (directory root, retention, etc.)
- DI wiring for concrete store implementations
- ACP protocol mapping:
  - `session/new` creates a new persisted session
  - `session/load` resolves session id and triggers history replay
  - `session/list` projects persisted sessions into ACP `SessionInfo`

## Design principle

The harness should be reusable outside ACP (other adapters, UIs, tests). Therefore:

- **Persistence is a harness concern** (domain and format).
- **Location/configuration is a server concern**.

## Notes on event naming

We prefer event names without redundant suffixes like `Added`, since events already represent facts in an append-only log.

## Notes on ACP load semantics

- For `session/load`, ACP requires replay of conversation history via `session/update` before the `session/load` response is completed.
- When a `sessionId` is unknown/missing, we return JSON-RPC `-32602` (invalid params): missing session id is treated as invalid input.
