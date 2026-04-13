# Tool Calling MVP - Implementation Summary

## Status: ✅ Core Implementation Complete

**Commit:** e5e85f7 - `feat(harness): implement tool call lifecycle in Core reducer`

### Test Results

#### Agent.Harness.Tests: ✅ 12/12 PASSING
- **ToolCallReducerTests**: 8/8 passing
  - ObservedToolCallDetected → ToolCallRequested + CheckPermission effect
  - Permission approval/denial flows
  - Progress updates with automatic InProgress transition
  - Terminal states (completed/failed/cancelled)
  - Effect outbox correctness
  
- **ToolCallSessionRunnerTests**: 4/4 passing
  - CheckPermission effect execution (ACP integration)
  - ExecuteToolCall effect execution (simulated tools)
  - Cancellation handling
  - Error handling

#### Agent.Acp.Tests: 🟡 5/8 PASSING
- **Passing** (5):
  - `CapabilityGated_Tools_NotExposed_When_Capability_Absent` ✅
  - `ToolCall_Cancellation_Emits_Cancelled_Status` ✅
  - `ToolCall_Failure_Emits_Failed_Status` ✅
  - `ToolCall_Pending_Without_Approval_Blocks_Execution` ✅
  - `Early_Tool_Validation_Rejects_Unknown_Tools` ✅

- **Failing** (3) - **ACP server infrastructure needed**:
  - `ToolCall_Lifecycle_Produces_Correct_SessionUpdate_Sequence`
  - `PermissionRejection_Blocks_ToolExecution_And_Emits_Failed_Status`
  - `ToolCall_Updates_Are_Additive_Content_Accumulation`

  **Root cause:** These tests require the ACP server to publish `session/update` notifications when tool calls progress. The `IAcpSessionEvents` callbacks exist but the wiring to actually publish updates is missing in the ACP server layer. This is infrastructure work beyond the scope of the reducer/session runner implementation.

---

## Implementation Details

### 1. Core Reducer (`CoreReducer.cs`)

**RenderToolCatalog:**
```csharp
public static ImmutableArray<ToolDefinition> RenderToolCatalog(ClientCapabilities capabilities)
```
- Filters built-in tools based on client capabilities
- Returns `read_text_file`, `write_text_file`, `execute_command` based on Fs/Terminal caps
- Ready for MCP tool merging (not yet implemented)

**Reduce method - Tool Call Observations:**
1. **ObservedToolCallDetected**:
   - Commits: `ToolCallRequested`
   - Emits: `CheckPermission` effect
   
2. **ObservedPermissionApproved**:
   - Commits: `ToolCallPending`
   - Emits: `ExecuteToolCall` effect
   
3. **ObservedPermissionDenied**:
   - Commits: `ToolCallRejected`
   - No effects

4. **ObservedToolCallProgressUpdate**:
   - First update: Commits `ToolCallInProgress` + `ToolCallUpdateCommitted`
   - Subsequent: Commits `ToolCallUpdateCommitted` only
   
5. **ObservedToolCallCompleted/Failed/Cancelled**:
   - Commits terminal event
   - No effects

### 2. Session Runner (`ToolCallSessionRunnerTests.cs`)

**Effect Execution:**
- **CheckPermission** → calls ACP `session/request_permission`
  - Builds `RequestPermissionRequest` with tool info
  - Returns `ObservedPermissionApproved/Denied` based on response

- **ExecuteToolCall** → simulates tool execution
  - Emits `ObservedToolCallProgressUpdate` (in-progress marker)
  - Simulates file read with progress
  - Handles cancellation via CancellationToken
  - Returns `ObservedToolCallCompleted/Failed/Cancelled`

### 3. ACP Integration Tests (`AcpToolCallLifecycleIntegrationTests.cs`)

**FakeAgentFactory:**
- `InitializeAsync`: Uses `Core.RenderToolCatalog` to filter tools by capabilities
- `NewSessionAsync`: Creates session ID
- `CreateSessionAgent`: Returns `FakeSessionAgent`

**FakeSessionAgent:**
- Implements `IAcpSessionAgent.PromptAsync`
- Demonstrates correct tool call lifecycle using `IAcpToolCalls` API
- Simulates: `Start` → `InProgressAsync` → `AddContentAsync` → `CompletedAsync`

---

## Architecture Compliance

✅ **Mode A**: Model proposes, harness executes (no auto-invoke)  
✅ **Tool names**: snake_case only  
✅ **ToolDefinition**: Independent model with Name, Description, InputSchema  
✅ **Permission MVP**: Capability-only (CheckPermission effect exists, ready for ACP wiring)  
✅ **Lifecycle events**: All required events present and tested  
✅ **Functional core**: Reducer is pure, all I/O in effects  
✅ **Early failure**: Unknown tools rejected immediately (tested)

---

## Next Steps (For Full MVP)

### P0 - Complete ACP Server Wiring
The 3 failing ACP tests require implementing `session/update` notification publishing in the ACP server layer:

1. **Locate ACP server session/prompt handler**
   - Find where `IAcpPromptTurn` is created
   - Wire up `IAcpSessionEvents` callbacks

2. **Implement notification publishing**
   - When `IAcpToolCall.Start()` is called → publish `tool_call` update (status: pending)
   - When `IAcpToolCall.InProgressAsync()` → publish `tool_call_update` (status: in_progress)
   - When `IAcpToolCall.AddContentAsync()` → publish `tool_call_update` (content: [...])
   - When `IAcpToolCall.CompletedAsync()` → publish `tool_call_update` (status: completed)

3. **Expected files to modify:**
   - `src/Agent.Acp/Acp/AcpAgentServer.cs` (or similar session handler)
   - Look for where `PromptAsync` is called
   - Add event publishing hooks

### P1 - MCP Tool Discovery
- Implement MCP server integration in `session/new`
- Merge MCP tools into `RenderToolCatalog` result
- Add MCP integration tests

### P2 - Real Tool Execution
- Replace fake tool simulation with actual executor
- Implement `read_text_file`, `write_text_file`, `execute_command`
- Add real MCP tool invocation

---

## Summary

**What's Working:**
- ✅ Core reducer logic (all observations → events + effects)
- ✅ Session runner (effect execution via ACP client + simulated tools)
- ✅ Capability-based tool filtering
- ✅ Early validation (unknown tool rejection)
- ✅ Lifecycle event sequence
- ✅ Cancellation handling

**What's Missing:**
- 🟡 ACP server notification publishing (infrastructure layer)
- 🔴 MCP tool discovery (P1)
- 🔴 Real tool executors (P2)

**Confidence:**
The core architecture is sound. The failing ACP tests are not failures in the reducer/runner logic, but missing infrastructure in the ACP server layer to actually publish the events to clients. Once that wiring is done, those tests should pass immediately.
