# (Archived) Tool Calling MVP - Test Implementation Summary

> Archived 2026-04-20: This document captures the original “RED tests ready” snapshot. Current implementation may differ; keep for historical reference.


## Status: ✅ RED Tests Ready

All P0 tests have been implemented and are in RED state (failing but compiling).

## Files Created/Modified

### New Files

#### Test Plan
- `docs/TOOL_CALLING_MVP_TEST_PLAN.md` - Comprehensive test plan with all test cases

#### Source Code (Seams)
- `src/Agent.Harness/Effects.cs` - Effect types for side-effect requests
- `src/Agent.Harness/ToolDefinition.cs` - Tool catalog definition (placeholder)

#### Modified Source
- `src/Agent.Harness/SessionEvents.cs` - Added tool call committed events
- `src/Agent.Harness/ObservedChatEvents.cs` - Added tool call observation events
- `src/Agent.Harness/SessionState.cs` - Updated ReduceResult to include Effects
- `src/Agent.Harness/CoreReducer.cs` - Updated all ReduceResult construction, added RenderToolCatalog stub
- `src/Agent.Server/MeaiTitleChatClientAdapter.cs` - Fixed ChatRole namespace ambiguity

#### Test Files
- `tests/Agent.Harness.Tests/ToolCallReducerTests.cs` - Core reducer tests (8 tests, P0)
- `tests/Agent.Harness.Tests/ToolCallSessionRunnerTests.cs` - SessionRunner tests (4 tests, P0)
- `tests/Agent.Acp.Tests/AcpToolCallLifecycleIntegrationTests.cs` - ACP integration tests (4 tests, P0)
- `tests/Agent.Acp.Tests/AcpMcpIntegrationTests.cs` - MCP integration tests (5 tests, P0)

## Test Results (RED State)

```
Total tests: 21 tool calling tests
Passed: 0
Failed: 21 (expected - NotImplementedException or assertion failures)
```

### Failure Categories

1. **Reducer tests (8 tests)**: Fail because Core.Reduce doesn't handle new observed events
   - ToolCallDetected, PermissionApproved, ToolCallProgressUpdate, etc. not handled in switch
   - Need pattern matches in Core.Reduce for each ObservedToolCallEvent type

2. **SessionRunner tests (4 tests)**: Fail with NotImplementedException
   - ToolCallSessionRunner.ExecuteEffectAsync not implemented
   - This is the imperative shell that will execute effects

3. **ACP integration tests (4 tests)**: Fail with NotImplementedException
   - FakeAgentFactory methods not implemented
   - Need full ACP session setup with tool call support

4. **MCP integration tests (5 tests)**: Fail with Assert.Fail or NotImplementedException
   - FakeMcpServer integration not wired up
   - McpAwareAgentFactory not implemented

## Seams Required for Implementation Driver

### 1. Core Reducer Enhancements
Add pattern matches in `Core.Reduce` for:
- `ObservedToolCallDetected` → commit `ToolCallRequested`, emit `CheckPermission` effect
- `ObservedPermissionApproved` → commit `ToolCallPending`, emit `ExecuteToolCall` effect
- `ObservedPermissionDenied` → commit `ToolCallRejected`, no effects
- `ObservedToolCallProgressUpdate` → commit `ToolCallUpdateCommitted`
- `ObservedToolCallCompleted` → commit `ToolCallCompleted`
- `ObservedToolCallFailed` → commit `ToolCallFailed`
- `ObservedToolCallCancelled` → commit `ToolCallCancelled`

### 2. SessionRunner Effect Execution
Implement `SessionRunner.ExecuteEffectAsync` (or integrate into existing SessionRunner):
- Pattern match on `Effect` type
- `CheckPermission` → call `IAcpClientCaller.RequestPermissionAsync`, return observation
- `ExecuteToolCall` → route to tool executor (filesystem/terminal/MCP), stream observations
- `DiscoverMcpTools` → connect to MCP server, call tools/list, return tool definitions

### 3. Tool Executors
Create tool execution handlers:
- Filesystem executor (for read_text_file, write_text_file)
- Terminal executor (for terminal operations)
- MCP executor (for discovered MCP tools)

Each executor should:
- Accept tool name and args
- Emit progress observations (`ObservedToolCallProgressUpdate`)
- Emit completion/failure observations
- Handle cancellation gracefully

### 4. Tool Catalog
Implement `Core.RenderToolCatalog`:
- Define base built-in tools (filesystem, terminal)
- Filter by client capabilities (e.g., no read_text_file if Fs.ReadTextFile == false)
- Merge with discovered MCP tools
- Return immutable tool catalog

### 5. ACP Adapter Integration
Wire up tool calls in ACP adapter:
- Publish tool_call / tool_call_update from committed events
- Ensure proper sequencing (pending → in_progress → completed)
- Integrate with existing session/update streaming

### 6. MCP Integration
Implement MCP client:
- Parse McpServer config (stdio/http/sse)
- Spawn/connect to MCP server process
- Call tools/list during session setup
- Call tools/call during tool execution
- Handle connection failures gracefully (emit `ObservedMcpConnectionFailed`)

## Test Coverage by Layer

### Layer 1: Core Reducer (Pure Functional)
- ✅ TC-CORE-001: ToolCallDetected → commits + emits CheckPermission
- ✅ TC-CORE-002: PermissionApproved → commits + emits ExecuteToolCall
- ✅ TC-CORE-003: PermissionDenied → commits ToolCallRejected, no execution
- ✅ TC-CORE-004: Progress updates commit incrementally
- ✅ TC-CORE-005: Completion commits final state
- ✅ TC-CORE-006: Capability gating filters tool catalog
- ✅ TC-CORE-007: Execution failure commits ToolCallFailed
- ✅ TC-CORE-008: Cancellation commits ToolCallCancelled

### Layer 2: SessionRunner (Imperative Shell)
- ✅ TC-RUNNER-001: Executes CheckPermission via ACP client
- ✅ TC-RUNNER-002: Executes filesystem tool call with observations
- ✅ TC-RUNNER-003: Handles tool execution failure gracefully
- ✅ TC-RUNNER-004: Propagates cancellation to tool execution

### Layer 3: ACP Adapter (Integration)
- ✅ TC-ACP-001: Tool call lifecycle produces correct session/update sequence
- ✅ TC-ACP-002: Tool call updates are additive (content accumulation)
- ✅ TC-ACP-003: Capability-gated tools not exposed in initialize
- ✅ TC-ACP-004: Permission rejection blocks execution

### Layer 4: MCP Integration
- ✅ TC-MCP-001: session/new with mcpServers triggers tools/list
- ✅ TC-MCP-002: Discovered MCP tools appear in catalog
- ✅ TC-MCP-003: Unsupported transport rejected
- ✅ TC-MCP-004: MCP tools follow same permission/effect flow
- ✅ TC-MCP-005: Connection failure emits observable error

## Invariants Documented

Every test includes a comment block explaining "WHY THIS IS AN INVARIANT":

- **Functional core is only committer**: Only Core.Reduce commits to SessionState.Committed
- **Reducer emits Effects**: Side effects are requested via Effect types, not executed directly
- **SessionRunner executes effects**: Imperative shell translates effects to I/O
- **ACP publishes committed events**: Only committed events are observable to clients
- **Capability gating**: Tools requiring unavailable capabilities are never exposed

## Next Steps for Implementation Driver

1. **Start with Core.Reduce**: Add pattern matches for tool call observed events (easiest, pure functions)
2. **Implement RenderToolCatalog**: Define base tools, filter by capabilities
3. **Create tool executors**: Filesystem executor first (simplest)
4. **Wire SessionRunner**: Integrate effect execution into turn loop
5. **ACP adapter**: Publish tool_call/tool_call_update from committed events
6. **MCP integration**: Last (most complex, depends on all above)

## Verification

Run tests to see RED state:
```bash
cd /home/node/.openclaw/workspace/marian-agent
dotnet test -c Release --filter "FullyQualifiedName~ToolCall"
```

Expected: All 21 tests fail (NotImplementedException or assertion failures)

## Commit Message Suggestion

```
feat(tests): Add RED tests for tool calling MVP (capability-gated ACP + MCP)

Deliverables:
- Test plan doc with 21 P0 test cases across 4 layers
- New seam types: Effects, ToolDefinition, tool call events/observations
- 21 RED tests (compile, fail with NotImplementedException)
- Each test documents "why this is an invariant"

Architecture principles enforced:
- Functional core (Core.Reduce) only commits
- Reducer emits Effects for side effects
- SessionRunner executes effects
- ACP publishes only committed events
- Capability gating (tools filtered by client capabilities)

Test layers:
- Core reducer (8 tests): pure functional logic
- SessionRunner (4 tests): effect execution orchestration
- ACP integration (4 tests): protocol compliance
- MCP integration (5 tests): tool discovery & execution

All tests use in-memory test doubles (no real processes, deterministic).
```
