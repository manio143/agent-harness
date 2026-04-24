# Thread Capabilities (Per-Thread Tool Surface)

## Goal

Support **per-thread capability restrictions** so the main thread (assumed “full power”) can create child threads whose tool surface is reduced.

Examples:
- A child thread that **cannot call `thread_*` tools**.
- A child thread that can **read files but not write**.
- A child thread that can call only **specific MCP servers** (or `mcp:*`).

This is **separate** from ACP client capabilities.

- **ACP client capabilities** answer: *“What tools may this client/session expose at all?”*
- **Thread capabilities** answer: *“Given the session’s available tools, what subset is this particular thread allowed to see/call?”*

## Non-goals

- Not a full sandbox. If `execute_command` is allowed, the thread can often bypass “fs.write” restrictions using shell redirection.
- Not an OS-level ACL.
- Not fine-grained file path policies (e.g., allow read only under a directory) — could be added later.

## Key decisions

### 1) Offer-time filtering + exec-time enforcement

Thread capabilities are enforced in two layers:

1) **Offer-time**: tool schemas provided to the model in `call_model` are filtered per thread.
   - The model generally won’t attempt to call tools it cannot see.

2) **Exec-time** (defense in depth): tool execution is gated per thread.
   - If a model hallucinates a tool call, it fails deterministically (`tool_not_allowed:<toolName>`).

### 2) Capability groups (Option 1A) + MCP per-server selectors

Capabilities are expressed as **selectors** (strings) in two lists:

- `allow: [...]`
- `deny: [...]`

Selectors can target:

- Built-in groups:
  - `threads` (all `thread_*` tools)
  - `fs.read`
  - `fs.write`
  - `host.exec`

- MCP servers:
  - `mcp:*` (all MCP servers)
  - `mcp:<serverId>` (a specific MCP server)

The MCP server id is derived from tool names exposed by discovery: `${serverId}__${toolId}`.

### 3) Precedence rules (allow + deny)

Precedence is:

1) Start from a base set:
   - If `allow` is non-empty: start from **empty**.
   - Else: start from **inherited** (or “full” for main).

2) Apply `allow` additions.
3) Apply `deny` removals (deny always wins).

Finally, intersect with ACP client capabilities:

```
EffectiveTools(thread) = ClientAllowedTools ∩ ToolsAllowedByThreadCapabilities
```

### 4) No capability listing in the system prompt

We do **not** need to list capabilities in the thread system prompt.

Because the tool catalog is filtered, the model learns capabilities implicitly from what is available.

(We still keep the existing threading guidance prompt fragment; this design does not require adding a capability fragment.)

## Data model

### ThreadMetadata

Add an optional capabilities spec to thread metadata (persisted in `thread.json`).

```csharp
public sealed record ThreadCapabilitiesSpec(
    ImmutableArray<string> Allow,
    ImmutableArray<string> Deny);
```

- `Allow` and `Deny` may be empty.
- Absence means “inherit from parent” (child) or “full” (main).

### Inheritance

When a child thread is created, its effective capabilities are:

- Parent effective capabilities
- plus overrides from `ThreadCapabilitiesSpec`.

This implies a *monotonic* default: a child cannot gain tools the parent didn’t have (because final intersection includes parent-derived set).

## Thread creation API

### `thread_start`

Extend `thread_start` args with optional `capabilities`:

```json
{
  "name": "doc",
  "context": "new",
  "mode": "single",
  "delivery": "immediate",
  "message": "...",
  "capabilities": {
    "allow": ["fs.read", "mcp:everything"],
    "deny": ["threads", "fs.write", "host.exec"]
  }
}
```

Notes:
- Main thread may omit capabilities entirely.
- Children default to inheriting parent capabilities.

## Enforcement points

### A) Offer-time filtering (tool catalog)

When preparing a `CallModel` for a given thread:

1) Start from the tool catalog already determined by ACP client capabilities.
2) Filter by thread capabilities.
3) Pass the filtered tool list into MEAI as `ToolDefinition[]`.

For MCP tools, determine the server id by splitting on `"__"`.

### B) Exec-time enforcement (router)

Before dispatching a tool call:
- Compute `IsToolAllowed(threadId, toolName)`.
- If not allowed, emit `ObservedToolCallFailed(toolId, "tool_not_allowed:<toolName>")`.

This must apply to:
- system tools (`thread_*`, `report_intent`, etc.)
- host tools (fs + exec)
- MCP tools

### Always-allowed tool(s)

`report_intent` should remain **always allowed** for any thread that is allowed to call tools at all.

Rationale: tool calling policy requires it before any other tool call.

## Persistence / “metadata projected from event log”

Thread metadata in this harness is already a hybrid:

- The **committed event log** is append-only and reducer-owned.
- Thread metadata (`thread.json`) stores durable, non-event data (e.g., `Mode`, `Model`, closure fields).

Thread capabilities follow the same pattern:
- Persist in `thread.json` (metadata), set at thread creation time.
- Do not require a committed event.

If we ever want strict event-sourced thread metadata, we can introduce a committed event like `ThreadCapabilitiesConfigured(...)` and project it into metadata. This design does not require that.

## Migration / compatibility

- If older `thread.json` files lack capabilities:
  - main thread defaults to “full”
  - child threads default to inherit parent

## Testing strategy (TDD)

1) **Offer-time filtering integration test**
   - Create a child thread with `deny:["threads"]`.
   - Verify that its model call does not include any `thread_*` tools.

2) **Exec-time enforcement integration test**
   - Script a model response that attempts a disallowed tool call.
   - Assert `ObservedToolCallFailed` with `tool_not_allowed:<toolName>`.

3) **MCP per-server filtering**
   - With discovered MCP tools `everything__*` and `files__*`, deny `mcp:files` but allow `mcp:*` minus `mcp:files`.
   - Assert only the allowed MCP tools are offered and executable.

## Open questions

- Do we want a dedicated error code namespace?
  - e.g. `thread_capability.tool_not_allowed:<tool>` vs generic `tool_not_allowed:<tool>`.

- Should we support pattern selectors beyond server-level for MCP?
  - e.g. `mcp:everything:get_sum` or `mcp:everything:*`.

- Should `thread_start` default-deny `threads` in child threads (to avoid nesting) or keep current permissive behavior?
