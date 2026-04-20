# (Archived) Implementation Driver Guide - Tool Calling MVP

> Archived 2026-04-20: This guide describes an earlier “RED tests” implementation phase. The harness has since evolved; keep this only for historical context.


This guide helps the implementation driver (you or another agent) implement the tool calling MVP by following the tests.

## Quick Start

1. **Run tests to see RED state:**
   ```bash
   cd /home/node/.openclaw/workspace/marian-agent
   dotnet test -c Release --filter "FullyQualifiedName~ToolCall"
   ```

2. **Pick a layer to implement** (recommended order below)

3. **Make tests GREEN one at a time**

4. **Commit after each passing test or logical chunk**

## Recommended Implementation Order

### Phase 1: Core Reducer (Pure Functions) - EASIEST
Start here. No I/O, no side effects, just pattern matching.

**Goal:** Make reducer tests pass

**Files to modify:**
- `src/Agent.Harness/CoreReducer.cs`

**Changes needed:**
```csharp
public static ReduceResult Reduce(SessionState state, ObservedChatEvent evt, CoreOptions? options = null)
{
    return evt switch
    {
        // Existing cases...
        
        // NEW: Tool call lifecycle
        ObservedToolCallDetected d => ReduceToolCallDetected(state, d),
        ObservedPermissionApproved a => ReducePermissionApproved(state, a),
        ObservedPermissionDenied d => ReducePermissionDenied(state, d),
        ObservedToolCallProgressUpdate u => ReduceToolCallProgress(state, u),
        ObservedToolCallCompleted c => ReduceToolCallCompleted(state, c),
        ObservedToolCallFailed f => ReduceToolCallFailed(state, f),
        ObservedToolCallCancelled x => ReduceToolCallCancelled(state, x),
        
        // Existing default...
    };
}

private static ReduceResult ReduceToolCallDetected(SessionState state, ObservedToolCallDetected d)
{
    var committed = new ToolCallRequested(d.ToolId, d.ToolName, d.Args);
    var effect = new CheckPermission(d.ToolId, d.ToolName, d.Args);
    
    var nextState = state with { Committed = state.Committed.Add(committed) };
    return new ReduceResult(
        nextState,
        ImmutableArray.Create<SessionEvent>(committed),
        ImmutableArray.Create<Effect>(effect));
}

private static ReduceResult ReducePermissionApproved(SessionState state, ObservedPermissionApproved a)
{
    // Find the original ToolCallRequested to get tool name & args
    var requested = state.Committed.OfType<ToolCallRequested>()
        .FirstOrDefault(r => r.ToolId == a.ToolId);
    
    if (requested is null)
        return new ReduceResult(state, ImmutableArray<SessionEvent>.Empty, ImmutableArray<Effect>.Empty);
    
    var pending = new ToolCallPending(a.ToolId);
    var effect = new ExecuteToolCall(a.ToolId, requested.ToolName, requested.Args);
    
    var nextState = state with { Committed = state.Committed.Add(pending) };
    return new ReduceResult(
        nextState,
        ImmutableArray.Create<SessionEvent>(pending),
        ImmutableArray.Create<Effect>(effect));
}

// Continue for other tool call events...
```

**Tests to pass:**
- `ToolCallDetected_Commits_ToolCallRequested_And_Emits_CheckPermission_Effect`
- `PermissionApproved_Commits_ToolCallPending_And_Emits_ExecuteToolCall_Effect`
- `PermissionDenied_Commits_ToolCallRejected_And_Emits_No_ExecutionEffect`
- `ToolExecutionProgress_Commits_IncrementalUpdates`
- `ToolExecutionCompleted_Commits_FinalState`
- `ToolExecutionFailed_Commits_ToolCallFailed`
- `ToolCallCancelled_Commits_ToolCallCancelled`

### Phase 2: Tool Catalog (Pure Function)

**Goal:** Implement capability-gated tool catalog

**Files to modify:**
- `src/Agent.Harness/CoreReducer.cs` (or create `src/Agent.Harness/ToolCatalog.cs`)
- `src/Agent.Harness/ToolDefinition.cs` (flesh out)

**Changes needed:**
```csharp
public static ImmutableArray<ToolDefinition> RenderToolCatalog(ClientCapabilities capabilities)
{
    var tools = ImmutableArray.CreateBuilder<ToolDefinition>();
    
    // Built-in filesystem tools
    if (capabilities.Fs?.ReadTextFile == true)
    {
        tools.Add(new ToolDefinition(
            Name: "read_text_file",
            Schema: new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "File path to read" },
                },
                required = new[] { "path" },
            }));
    }
    
    if (capabilities.Fs?.WriteTextFile == true)
    {
        tools.Add(new ToolDefinition(
            Name: "write_text_file",
            Schema: new { /* ... */ }));
    }
    
    // Terminal tools
    if (capabilities.Terminal == true)
    {
        tools.Add(new ToolDefinition(
            Name: "execute_command",
            Schema: new { /* ... */ }));
    }
    
    return tools.ToImmutable();
}
```

**Test to pass:**
- `ToolCatalog_ExcludesTools_WhenCapabilityAbsent`

### Phase 3: Tool Executors (Imperative Shell)

**Goal:** Create tool execution handlers

**Files to create:**
- `src/Agent.Harness/Tools/FilesystemToolExecutor.cs`
- `src/Agent.Harness/Tools/TerminalToolExecutor.cs`
- `src/Agent.Harness/Tools/IToolExecutor.cs`

**Example:**
```csharp
public interface IToolExecutor
{
    Task<ImmutableArray<ObservedChatEvent>> ExecuteAsync(
        string toolId,
        object args,
        CancellationToken cancellationToken);
}

public class FilesystemToolExecutor : IToolExecutor
{
    private readonly IAcpClientCaller _client;
    
    public FilesystemToolExecutor(IAcpClientCaller client)
    {
        _client = client;
    }
    
    public async Task<ImmutableArray<ObservedChatEvent>> ExecuteAsync(
        string toolId,
        object args,
        CancellationToken cancellationToken)
    {
        var observations = ImmutableArray.CreateBuilder<ObservedChatEvent>();
        
        try
        {
            // Emit in-progress
            observations.Add(new ObservedToolCallProgressUpdate(toolId, new { status = "reading" }));
            
            // Call ACP client to read file
            var readReq = new ReadTextFileRequest
            {
                SessionId = /* get from context */,
                Path = args.path,
            };
            
            var result = await _client.ReadTextFileAsync(readReq, cancellationToken);
            
            // Emit completion
            observations.Add(new ObservedToolCallCompleted(toolId, new { content = result.Content }));
        }
        catch (Exception ex)
        {
            observations.Add(new ObservedToolCallFailed(toolId, ex.Message));
        }
        
        return observations.ToImmutable();
    }
}
```

### Phase 4: SessionRunner Integration

**Goal:** Wire effect execution into SessionRunner

**Files to modify:**
- `src/Agent.Harness/SessionRunner.cs`
- `src/Agent.Harness/TurnRunner.cs` (if separate)

**Changes needed:**
```csharp
public class SessionRunner
{
    private readonly Dictionary<string, IToolExecutor> _toolExecutors;
    
    // ... existing code ...
    
    private async Task ExecuteEffectsAsync(
        ImmutableArray<Effect> effects,
        CancellationToken cancellationToken)
    {
        foreach (var effect in effects)
        {
            var observations = effect switch
            {
                CheckPermission cp => await ExecuteCheckPermissionAsync(cp, cancellationToken),
                ExecuteToolCall etc => await ExecuteToolCallAsync(etc, cancellationToken),
                DiscoverMcpTools dmt => await DiscoverMcpToolsAsync(dmt, cancellationToken),
                _ => ImmutableArray<ObservedChatEvent>.Empty,
            };
            
            // Feed observations back to reducer
            foreach (var obs in observations)
            {
                var result = Core.Reduce(_state, obs);
                _state = result.Next;
                
                // Publish newly committed events via ACP
                await PublishCommittedEventsAsync(result.NewlyCommitted, cancellationToken);
                
                // Recurse: execute any new effects
                if (!result.Effects.IsEmpty)
                {
                    await ExecuteEffectsAsync(result.Effects, cancellationToken);
                }
            }
        }
    }
    
    private async Task<ImmutableArray<ObservedChatEvent>> ExecuteCheckPermissionAsync(
        CheckPermission cp,
        CancellationToken cancellationToken)
    {
        var req = new RequestPermissionRequest
        {
            SessionId = _sessionId,
            ToolCall = new ToolCallUpdate { ToolCallId = cp.ToolId },
            Options = new List<PermissionOption>
            {
                new PermissionOption
                {
                    OptionId = "allow-once",
                    Name = "Allow once",
                    Kind = PermissionOptionKind.AllowOnce,
                },
                new PermissionOption
                {
                    OptionId = "reject-once",
                    Name = "Reject",
                    Kind = PermissionOptionKind.RejectOnce,
                },
            },
        };
        
        var resp = await _client.RequestPermissionAsync(req, cancellationToken);
        
        if (resp.Outcome.Outcome == RequestPermissionOutcomeOutcome.Selected)
        {
            return ImmutableArray.Create<ObservedChatEvent>(
                new ObservedPermissionApproved(cp.ToolId));
        }
        else
        {
            return ImmutableArray.Create<ObservedChatEvent>(
                new ObservedPermissionDenied(cp.ToolId, "User cancelled or rejected"));
        }
    }
    
    private async Task<ImmutableArray<ObservedChatEvent>> ExecuteToolCallAsync(
        ExecuteToolCall etc,
        CancellationToken cancellationToken)
    {
        if (!_toolExecutors.TryGetValue(etc.ToolName, out var executor))
        {
            return ImmutableArray.Create<ObservedChatEvent>(
                new ObservedToolCallFailed(etc.ToolId, $"Unknown tool: {etc.ToolName}"));
        }
        
        return await executor.ExecuteAsync(etc.ToolId, etc.Args, cancellationToken);
    }
}
```

**Tests to pass:**
- `SessionRunner_Executes_CheckPermission_Effect_Via_AcpClient`
- `SessionRunner_Executes_FilesystemToolCall_And_Streams_Observations`
- `SessionRunner_Handles_FilesystemToolCall_Failure_Gracefully`
- `SessionRunner_Propagates_Cancellation_To_ToolExecution`

### Phase 5: ACP Adapter (Protocol Compliance)

**Goal:** Publish tool_call/tool_call_update from committed events

**Files to modify:**
- `src/Agent.Harness/Acp/AcpSessionAgentAdapter.cs` (or wherever session/update is sent)

**Changes needed:**

Already have `AcpToolCallTracker` - just need to integrate:

```csharp
private async Task PublishCommittedEventAsync(SessionEvent evt, CancellationToken cancellationToken)
{
    switch (evt)
    {
        // Existing cases for messages, deltas, etc.
        
        case ToolCallRequested req:
            var call = _toolCalls.Start(req.ToolId, req.ToolName, ToolKind.Other);
            break;
            
        case ToolCallPending p:
            // Tracker already emitted pending in Start()
            break;
            
        case ToolCallInProgress ip:
            var handle = GetToolCallHandle(ip.ToolId);
            await handle.InProgressAsync(cancellationToken);
            break;
            
        case ToolCallUpdateCommitted uc:
            var h = GetToolCallHandle(uc.ToolId);
            await h.AddContentAsync(MapContentToToolCallContent(uc.Content), cancellationToken);
            break;
            
        case ToolCallCompleted c:
            var hc = GetToolCallHandle(c.ToolId);
            await hc.CompletedAsync(cancellationToken);
            break;
            
        case ToolCallFailed f:
            var hf = GetToolCallHandle(f.ToolId);
            await hf.FailedAsync(f.Error, cancellationToken);
            break;
            
        case ToolCallCancelled x:
            var hx = GetToolCallHandle(x.ToolId);
            await hx.CancelledAsync(cancellationToken);
            break;
    }
}
```

**Tests to pass:**
- `ToolCall_Lifecycle_Produces_Correct_SessionUpdate_Sequence`
- `ToolCall_Updates_Are_Additive_Content_Accumulation`
- `CapabilityGated_Tools_NotExposed_When_Capability_Absent`
- `PermissionRejection_Blocks_ToolExecution_And_Emits_Failed_Status`

### Phase 6: MCP Integration (Most Complex)

**Goal:** Discover and execute MCP tools

**Files to create:**
- `src/Agent.Harness/Mcp/McpClient.cs`
- `src/Agent.Harness/Mcp/McpStdioTransport.cs`
- `src/Agent.Harness/Mcp/McpToolExecutor.cs`

**Changes needed:**

1. **During session/new:** Parse mcpServers, spawn processes, call tools/list
2. **Add discovered tools to catalog**
3. **Route MCP tool calls to MCP executor**

**Tests to pass:**
- `SessionNew_WithMcpServers_Triggers_ToolsList_Discovery`
- `DiscoveredMcpTools_AppearIn_ToolCatalog`
- `UnsupportedMcpTransport_Rejected_During_SessionNew`
- `McpToolExecution_Follows_Same_PermissionEffect_Flow`
- `McpConnectionFailure_Emits_ObservableError_And_Continues_Session`

## Tips

### Debugging RED Tests
- Each test has a "WHY THIS IS AN INVARIANT" comment - read it to understand the goal
- Tests use in-memory test doubles - no real I/O, easy to debug
- Use `dotnet test --filter "FullyQualifiedName~SpecificTestName" --logger "console;verbosity=detailed"` for verbose output

### Maintaining Invariants
- **Never commit events from outside Core.Reduce**
- **Never execute side effects from Core.Reduce**
- Effects flow: `Core.Reduce → Effects → SessionRunner → Observations → Core.Reduce`
- Tool calls follow the same event/effect flow as everything else

### Testing Strategy
- Make one test green at a time
- Commit after each passing test or small logical chunk
- Reducer tests are easiest (pure functions)
- Integration tests confirm the whole stack works

### Common Pitfalls
- **Forgetting to emit effects**: ReduceResult must include Effects array
- **Executing side effects in reducer**: Use Effects instead
- **Publishing uncommitted events**: ACP adapter must only read from SessionState.Committed
- **Not handling cancellation**: Every async executor must respect CancellationToken

## Success Criteria

All 21 tests pass:
```bash
dotnet test -c Release --filter "FullyQualifiedName~ToolCall"
# Expected: Passed: 21, Failed: 0
```

## Documentation

After implementation, update:
- `README.md` - Add tool calling feature description
- Architecture docs - Document effect execution flow
- Integration guide - How to add new tools

## Questions?

The test plan (`docs/TOOL_CALLING_MVP_TEST_PLAN.md`) has detailed descriptions of each test case and the invariants they enforce.
