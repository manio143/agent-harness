# Tool Calling MVP Test Plan

## Overview

This test plan covers the MVP implementation of tool calling with:
- **Capability-gated ACP filesystem/terminal** helpers
- **MCP discovery and execution** (Model Context Protocol)
- **Functional core principles**: only the reducer commits; reducer emits Effects; SessionRunner executes effects; ACP publishes only committed events

## Architecture Principles (Invariants)

### Core Principles
1. **Functional Core Owns State**: Only `CoreReducer` commits events to `SessionState.Committed`
2. **Effects as Output**: Reducer emits `Effect` values to request side effects (permissions, execution)
3. **SessionRunner as Executor**: SessionRunner executes effects and feeds observations back to reducer
4. **ACP Publishes Committed Only**: ACP adapter publishes only from `SessionState.Committed`
5. **Capability Gating**: Tool definitions are filtered by client capabilities before exposure

### Event/Effect Flow
```
ObservedEvent → CoreReducer → (CommittedEvent, Effect[]) → SessionRunner → Execute Effects → New ObservedEvents
                                         ↓
                                  SessionState.Committed → ACP Adapter → session/update notifications
```

## Test Layers

### Layer 1: Core Reducer (Pure Functional Tests)

These tests verify the reducer's pure transformation logic without any I/O.

#### TC-CORE-001: Tool Call Detection Commits ToolCallRequested + Emits CheckPermission Effect
**Why this is an invariant**: The reducer must translate observed tool call intents into committed state *and* emit the permission check as an effect. This ensures the functional core doesn't perform side effects directly.

**Given**: `SessionState` with existing committed messages  
**When**: `ObservedToolCallDetected(toolId="call_1", toolName="read_file", args={...})` is reduced  
**Then**:
- `ReduceResult.Next.Committed` contains new `ToolCallRequested(toolId, toolName, args)`
- `ReduceResult.Effects` contains `Effect.CheckPermission(toolId, toolName, args)`
- No other side effects occur

#### TC-CORE-002: Permission Approved Commits ToolCallPending + Emits ExecuteToolCall Effect
**Why this is an invariant**: Separation of concerns—reducer commits the state transition, emits the execution effect, but doesn't execute.

**Given**: `SessionState` with `ToolCallRequested` committed  
**When**: `ObservedPermissionApproved(toolId="call_1")` is reduced  
**Then**:
- `ReduceResult.Next.Committed` contains `ToolCallPending(toolId)` or `ToolCallInProgress(toolId)`
- `ReduceResult.Effects` contains `Effect.ExecuteToolCall(toolId)`

#### TC-CORE-003: Permission Denied Commits ToolCallRejected + No Execution Effect
**Why this is an invariant**: Rejected tools must be recorded but never executed.

**Given**: `SessionState` with `ToolCallRequested` committed  
**When**: `ObservedPermissionDenied(toolId="call_1", reason="user rejected")` is reduced  
**Then**:
- `ReduceResult.Next.Committed` contains `ToolCallRejected(toolId, reason)`
- `ReduceResult.Effects` is empty (no execution)

#### TC-CORE-004: Tool Execution Progress Updates Commit Incrementally
**Why this is an invariant**: Progress must be observable in committed state for reproducibility and debugging.

**Given**: `SessionState` with `ToolCallInProgress` committed  
**When**: `ObservedToolCallProgressUpdate(toolId="call_1", content="Processing...")` is reduced  
**Then**:
- `ReduceResult.Next.Committed` contains `ToolCallUpdateCommitted(toolId, content)`

#### TC-CORE-005: Tool Execution Completion Commits Final State
**Why this is an invariant**: Completion must finalize state and close the tool call lifecycle.

**Given**: `SessionState` with `ToolCallInProgress` committed  
**When**: `ObservedToolCallCompleted(toolId="call_1", result={...})` is reduced  
**Then**:
- `ReduceResult.Next.Committed` contains `ToolCallCompleted(toolId, result)`
- Tool call is marked as terminal (no further updates allowed)

#### TC-CORE-006: Capability Absent → Tool Not Included in Catalog
**Why this is an invariant**: Tools requiring capabilities the client doesn't have must never be exposed to the model.

**Given**: `ClientCapabilities.Fs.ReadTextFile = false`  
**When**: `RenderToolCatalog(capabilities)` is invoked  
**Then**:
- Tool catalog does NOT include `read_text_file` tool
- Tool catalog DOES include capability-independent tools

---

### Layer 2: SessionRunner / Orchestrator (Imperative Shell Tests)

These tests verify SessionRunner correctly executes effects and orchestrates the event loop.

#### TC-RUNNER-001: SessionRunner Executes CheckPermission Effect
**Why this is an invariant**: The SessionRunner must translate emitted effects into actual ACP `session/request_permission` calls.

**Given**: Mocked `IAcpClientCaller`  
**When**: SessionRunner processes `Effect.CheckPermission(toolId, toolName, args)`  
**Then**:
- `IAcpClientCaller.RequestPermissionAsync(...)` is called with correct parameters
- Response is translated to `ObservedPermissionApproved` or `ObservedPermissionDenied`
- Observation is fed back to reducer

#### TC-RUNNER-002: SessionRunner Executes ExecuteToolCall Effect (Filesystem Example)
**Why this is an invariant**: SessionRunner must route tool execution to the appropriate handler (e.g., filesystem, MCP).

**Given**: `Effect.ExecuteToolCall(toolId="call_1", toolName="read_text_file", args={path="/tmp/test.txt"})`  
**When**: SessionRunner executes the effect  
**Then**:
- Filesystem handler is invoked
- Progress observations (`ObservedToolCallProgressUpdate`, `ObservedToolCallCompleted`) are fed to reducer
- Final result is committed via reducer

#### TC-RUNNER-003: SessionRunner Handles Tool Execution Failure
**Why this is an invariant**: Failures must be observable and committed without crashing the session.

**Given**: `Effect.ExecuteToolCall(toolId="call_1", toolName="read_text_file", args={path="/nonexistent"})`  
**When**: Filesystem read fails  
**Then**:
- `ObservedToolCallFailed(toolId, error="File not found")` is fed to reducer
- Reducer commits `ToolCallFailed(toolId, error)`

#### TC-RUNNER-004: SessionRunner Respects Turn Cancellation
**Why this is an invariant**: Cancellation must propagate to in-flight tool calls and emit proper terminal states.

**Given**: Tool call in progress  
**When**: `CancellationToken` is cancelled  
**Then**:
- Tool execution is aborted
- `ObservedToolCallCancelled(toolId)` is fed to reducer
- Reducer commits `ToolCallCancelled(toolId)`

---

### Layer 3: ACP Adapter (In-Memory JSON-RPC Integration Tests)

These tests verify ACP protocol compliance using in-memory transports (no real processes).

#### TC-ACP-001: Tool Call Lifecycle Produces Correct session/update Sequence
**Why this is an invariant**: ACP clients depend on the exact sequence of `tool_call` → `tool_call_update` → final `end_turn`.

**Given**: In-memory ACP client/server pair  
**When**: Agent processes a prompt that triggers a tool call (mocked model response)  
**Then**: Client receives session/update notifications in order:
1. `{sessionUpdate: "tool_call", toolCallId, title, kind, status: "pending"}`
2. `{sessionUpdate: "tool_call_update", toolCallId, status: "in_progress"}`
3. `{sessionUpdate: "tool_call_update", toolCallId, content: [...]}`
4. `{sessionUpdate: "tool_call_update", toolCallId, status: "completed"}`
5. Final prompt response with `stopReason: "end_turn"`

#### TC-ACP-002: Tool Call Updates Are Additive (Content Accumulation)
**Why this is an invariant**: ACP spec requires tool_call_update content to be incremental, not replacement.

**Given**: In-memory ACP session with tool call in progress  
**When**: Multiple `tool_call_update` notifications with content are sent  
**Then**:
- Each update appends to content array
- Client can reconstruct full output by concatenating updates

#### TC-ACP-003: Capability-Gated Tools Not Exposed in initialize Response
**Why this is an invariant**: Agents must not advertise tools that require unavailable client capabilities.

**Given**: Client initializes with `ClientCapabilities.Fs = null` (no filesystem capability)  
**When**: Agent responds to `initialize` request  
**Then**:
- `AgentCapabilities.PromptCapabilities.Tools` does NOT include filesystem tools
- Other tools (e.g., MCP-discovered, non-capability-dependent) are included

#### TC-ACP-004: Permission Request → Rejection → No Tool Execution
**Why this is an invariant**: User rejection must block execution and be observable in session updates.

**Given**: In-memory ACP session, client rejects permission with `outcome: "rejected_once"`  
**When**: Agent requests permission for a tool call  
**Then**:
- `session/update` shows `tool_call_update` with `status: "failed"`
- No actual tool execution occurs
- Session continues (not terminated)

---

### Layer 4: MCP Integration (Fake MCP Server Tests)

These tests verify MCP discovery and execution using a fake in-process stdio MCP server.

#### TC-MCP-001: session/new with mcpServers Triggers tools/list Discovery
**Why this is an invariant**: Agents must discover MCP tools during session setup, not lazily.

**Given**: In-memory ACP session, `NewSessionRequest.McpServers = [{stdio: {command: "fake-mcp-server"}}]`  
**When**: Agent processes session/new  
**Then**:
- Fake MCP server's `tools/list` method is called
- Discovered tools are added to the session's tool catalog
- Tools appear in subsequent prompt capabilities

#### TC-MCP-002: Discovered MCP Tools Appear in Tool Catalog
**Why this is an invariant**: MCP tools must be usable in prompts after discovery.

**Given**: Fake MCP server advertising `{"name": "fetch_weather", "inputSchema": {...}}`  
**When**: Tool catalog is rendered for the session  
**Then**:
- Tool catalog includes `fetch_weather` with correct schema

#### TC-MCP-003: Unsupported MCP Transport Rejected During session/new
**Why this is an invariant**: Agents must validate MCP transport capabilities and fail fast.

**Given**: `NewSessionRequest.McpServers = [{http: {url: "https://mcp.example.com"}}]`  
**When**: Agent lacks HTTP MCP transport capability  
**Then**:
- `session/new` returns error (or rejects that specific server)
- Error message indicates unsupported transport

#### TC-MCP-004: MCP Tool Execution Follows Same Permission/Effect Flow
**Why this is an invariant**: MCP tools must integrate into the same reducer/effect architecture as built-in tools.

**Given**: Fake MCP server with `fetch_weather` tool  
**When**: Model requests `fetch_weather` and permission is granted  
**Then**:
- Reducer emits `Effect.ExecuteToolCall(toolId, toolName="fetch_weather", ...)`
- SessionRunner routes to MCP executor
- MCP `tools/call` is invoked on the fake server
- Results flow back through `ObservedToolCallCompleted`

#### TC-MCP-005: MCP Server Connection Failure Emits Observable Error
**Why this is an invariant**: Connection failures must not crash the session; they must be observable and recoverable.

**Given**: `NewSessionRequest.McpServers = [{stdio: {command: "nonexistent-binary"}}]`  
**When**: Agent attempts to spawn MCP server  
**Then**:
- `ObservedMcpConnectionFailed(serverId, error)` is fed to reducer
- Session continues (graceful degradation)
- Error is logged/observable to client

---

## Test Implementation Notes

### Seams Required for Implementation

To make these tests compile and useful as RED tests, we need:

1. **New `SessionEvent` subtypes** (in `SessionEvents.cs`):
   - `ToolCallRequested(string ToolId, string ToolName, object Args)`
   - `ToolCallPending(string ToolId)`
   - `ToolCallInProgress(string ToolId)`
   - `ToolCallUpdateCommitted(string ToolId, object Content)`
   - `ToolCallCompleted(string ToolId, object Result)`
   - `ToolCallFailed(string ToolId, string Error)`
   - `ToolCallRejected(string ToolId, string Reason)`
   - `ToolCallCancelled(string ToolId)`

2. **New `ObservedChatEvent` subtypes** (in `ObservedChatEvents.cs`):
   - `ObservedToolCallDetected(string ToolId, string ToolName, object Args)`
   - `ObservedPermissionApproved(string ToolId)`
   - `ObservedPermissionDenied(string ToolId, string Reason)`
   - `ObservedToolCallProgressUpdate(string ToolId, object Content)`
   - `ObservedToolCallCompleted(string ToolId, object Result)`
   - `ObservedToolCallFailed(string ToolId, string Error)`
   - `ObservedToolCallCancelled(string ToolId)`
   - `ObservedMcpConnectionFailed(string ServerId, string Error)`

3. **Effect type** (new file `Effects.cs`):
   ```csharp
   public abstract record Effect;
   public sealed record CheckPermission(string ToolId, string ToolName, object Args) : Effect;
   public sealed record ExecuteToolCall(string ToolId, string ToolName, object Args) : Effect;
   ```

4. **Updated `ReduceResult`**:
   ```csharp
   public sealed record ReduceResult(
       SessionState Next,
       ImmutableArray<SessionEvent> NewlyCommitted,
       ImmutableArray<Effect> Effects);  // ← NEW
   ```

5. **Tool catalog rendering** (new method in `Core` or separate `ToolCatalog` class):
   ```csharp
   public static ImmutableArray<ToolDefinition> RenderToolCatalog(ClientCapabilities capabilities);
   ```

6. **Fake MCP server test helper** (in test project):
   - In-memory stdio transport stub
   - Minimal MCP JSON-RPC responder for `initialize`, `tools/list`, `tools/call`

### Test File Organization

- `tests/Agent.Harness.Tests/`:
  - `ToolCallReducerTests.cs` (TC-CORE-001 through TC-CORE-006)
  - `ToolCallSessionRunnerTests.cs` (TC-RUNNER-001 through TC-RUNNER-004)
  
- `tests/Agent.Acp.Tests/`:
  - `AcpToolCallLifecycleIntegrationTests.cs` (TC-ACP-001 through TC-ACP-004)
  - `AcpMcpIntegrationTests.cs` (TC-MCP-001 through TC-MCP-005)
  - `Helpers/FakeMcpServer.cs` (test helper)

### Determinism & Test Isolation

- **No real processes**: Use in-memory transports and fake MCP servers
- **Controlled time**: Use `CancellationTokenSource` with short timeouts for determinism
- **State immutability**: All state changes go through reducer (easy to snapshot/replay)
- **Explicit assertions**: Every test checks both state transitions AND observable outputs

---

## Priority Classification

### P0 (Must Have for MVP)
- TC-CORE-001, TC-CORE-002, TC-CORE-003, TC-CORE-005, TC-CORE-006
- TC-RUNNER-001, TC-RUNNER-002
- TC-ACP-001, TC-ACP-003, TC-ACP-004
- TC-MCP-001, TC-MCP-002, TC-MCP-003

### P1 (Should Have)
- TC-CORE-004 (progress updates)
- TC-RUNNER-003 (failure handling)
- TC-RUNNER-004 (cancellation)
- TC-ACP-002 (additive updates)
- TC-MCP-004, TC-MCP-005

### P2 (Nice to Have)
- Performance benchmarks for reducer
- Concurrent tool call tests
- MCP reconnection/retry logic

---

## Success Criteria

1. All P0 tests compile (RED state: fail with NotImplementedException or assertion failures)
2. Each test includes comment explaining "why this is an invariant"
3. No mocking frameworks beyond in-memory test doubles
4. Tests are deterministic (no sleeps, no real I/O, no flaky timing)
5. Clear seam interfaces documented for implementation driver
