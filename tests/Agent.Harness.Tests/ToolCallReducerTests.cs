using System.Collections.Immutable;
using System.Text.Json;
using Agent.Harness;

namespace Agent.Harness.Tests;

/// <summary>
/// Tests for tool calling reducer logic (functional core).
/// 
/// Invariant: The reducer is the ONLY component that commits events to SessionState.Committed.
/// The reducer emits Effects (CheckPermission, ExecuteToolCall) but never executes them directly.
/// </summary>
public class ToolCallReducerTests
{
    private static JsonElement J(object value) => JsonSerializer.SerializeToElement(value);

    /// <summary>
    /// TC-CORE-001: Tool Call Detection Commits ToolCallRequested + Emits CheckPermission Effect
    /// 
    /// WHY THIS IS AN INVARIANT:
    /// The reducer must translate observed tool call intents into committed state AND emit the
    /// permission check as an effect. This ensures the functional core doesn't perform side effects
    /// directly—it only declares what side effects are needed via the Effect type.
    /// </summary>
    [Fact]
    public void ToolCallDetected_Commits_ToolCallRequested_And_Emits_CheckPermission_Effect()
    {
        // ARRANGE: Initial state with existing messages
        var initial = new SessionState(
            Committed: ImmutableArray.Create<SessionEvent>(
                new UserMessage("Read file /tmp/test.txt")),
            Buffer: TurnBuffer.Empty,
            Tools: ImmutableArray.Create(ToolSchemas.ReadTextFile));

        var toolArgs = new { path = "/tmp/test.txt" };
        var observed = new ObservedToolCallDetected(
            ToolId: "call_1",
            ToolName: "read_text_file",
            Args: toolArgs);

        // ACT
        var result = Core.Reduce(initial, observed);

        // ASSERT: Committed event
        Assert.Contains(result.Next.Committed, evt => evt is ToolCallRequested req &&
            req.ToolId == "call_1" &&
            req.ToolName == "read_text_file" &&
            req.Args.GetProperty("path").GetString() == "/tmp/test.txt");

        // ASSERT: Effect emitted
        Assert.Contains(result.Effects, eff => eff is CheckPermission perm &&
            perm.ToolId == "call_1" &&
            perm.ToolName == "read_text_file" &&
            perm.Args == toolArgs);

        // ASSERT: No other side effects
        Assert.Single(result.Effects);
    }

    /// <summary>
    /// TC-CORE-002: Permission Approved Commits ToolCallPending + Emits ExecuteToolCall Effect
    /// 
    /// WHY THIS IS AN INVARIANT:
    /// Separation of concerns—the reducer commits the state transition (approved → pending/in-progress)
    /// and emits the execution effect, but doesn't execute the tool itself. The SessionRunner will
    /// execute the effect and feed observations back.
    /// </summary>
    [Fact]
    public void PermissionApproved_Commits_ToolCallPending_And_Emits_ExecuteToolCall_Effect()
    {
        // ARRANGE: State with ToolCallRequested already committed
        var toolArgs = new { path = "/tmp/test.txt" };
        var initial = new SessionState(
            Committed: ImmutableArray.Create<SessionEvent>(
                new UserMessage("Read file /tmp/test.txt"),
                new ToolCallRequested("call_1", "read_text_file", J(toolArgs))),
            Buffer: TurnBuffer.Empty,
            Tools: ImmutableArray.Create(ToolSchemas.ReadTextFile));

        var observed = new ObservedPermissionApproved(ToolId: "call_1", Reason: "capability_present");

        // ACT
        var result = Core.Reduce(initial, observed);

        // ASSERT: Committed event (either Pending or InProgress)
        Assert.Contains(result.Next.Committed, evt =>
            evt is ToolCallPending pending && pending.ToolId == "call_1");

        // ASSERT: ExecuteToolCall effect emitted
        Assert.Contains(result.Effects, eff => eff is ExecuteToolCall exec &&
            exec.ToolId == "call_1" &&
            exec.ToolName == "read_text_file");

        // ASSERT: Single effect
        Assert.Single(result.Effects);
    }

    /// <summary>
    /// TC-CORE-003: Permission Denied Commits ToolCallRejected + No Execution Effect
    /// 
    /// WHY THIS IS AN INVARIANT:
    /// Rejected tools must be recorded in committed state for auditability and reproducibility,
    /// but NEVER executed. The absence of an ExecuteToolCall effect ensures the tool is never run.
    /// </summary>
    [Fact]
    public void PermissionDenied_Commits_ToolCallRejected_And_Emits_No_ExecutionEffect()
    {
        // ARRANGE
        var toolArgs = new { path = "/etc/passwd" };
        var initial = new SessionState(
            Committed: ImmutableArray.Create<SessionEvent>(
                new UserMessage("Read /etc/passwd"),
                new ToolCallRequested("call_1", "read_text_file", J(toolArgs))),
            Buffer: TurnBuffer.Empty,
            Tools: ImmutableArray.Create(ToolSchemas.ReadTextFile));

        var observed = new ObservedPermissionDenied(
            ToolId: "call_1",
            Reason: "User rejected: sensitive file");

        // ACT
        var result = Core.Reduce(initial, observed);

        // ASSERT: Committed rejection
        Assert.Contains(result.Next.Committed, evt =>
            evt is ToolCallRejected rejected &&
            rejected.ToolId == "call_1" &&
            rejected.Reason == "User rejected: sensitive file" &&
            rejected.Details.IsEmpty);

        // ASSERT: NO ExecuteToolCall effect
        Assert.DoesNotContain(result.Effects, eff => eff is ExecuteToolCall);

        // ASSERT: Effects should be empty
        Assert.Empty(result.Effects);
    }

    /// <summary>
    /// TC-CORE-004: Tool Execution Progress Updates Commit Incrementally
    /// 
    /// WHY THIS IS AN INVARIANT:
    /// Progress must be observable in committed state for reproducibility, debugging, and streaming
    /// to ACP clients. Each progress update becomes a ToolCallUpdate event.
    /// </summary>
    [Fact]
    public void ToolExecutionProgress_Commits_IncrementalUpdates()
    {
        // ARRANGE: Tool call already in progress
        var initial = new SessionState(
            Committed: ImmutableArray.Create<SessionEvent>(
                new UserMessage("Read file"),
                new ToolCallRequested("call_1", "read_text_file", J(new { path = "/tmp/test.txt" })),
                new ToolCallPending("call_1"),
                new ToolCallInProgress("call_1")),
            Buffer: TurnBuffer.Empty,
            Tools: ImmutableArray.Create(ToolSchemas.ReadTextFile));

        var progressContent = new { text = "Reading line 1..." };
        var observed = new ObservedToolCallProgressUpdate(
            ToolId: "call_1",
            Content: progressContent);

        // ACT
        var result = Core.Reduce(initial, observed);

        // ASSERT: Progress update committed
        Assert.Contains(result.Next.Committed, evt =>
            evt is ToolCallUpdate update &&
            update.ToolId == "call_1" &&
            update.Content.GetProperty("text").GetString() == "Reading line 1...");

        // ASSERT: NewlyCommitted contains the update
        Assert.Single(result.NewlyCommitted);
        Assert.IsType<ToolCallUpdate>(result.NewlyCommitted[0]);
    }

    /// <summary>
    /// TC-CORE-005: Tool Execution Completion Commits Final State
    /// 
    /// WHY THIS IS AN INVARIANT:
    /// Completion must finalize state and close the tool call lifecycle. After ToolCallCompleted,
    /// no further updates should be allowed (enforced by SessionRunner/ACP adapter).
    /// </summary>
    [Fact]
    public void ToolExecutionCompleted_Commits_FinalState()
    {
        // ARRANGE
        var initial = new SessionState(
            Committed: ImmutableArray.Create<SessionEvent>(
                new UserMessage("Read file"),
                new ToolCallRequested("call_1", "read_text_file", J(new { path = "/tmp/test.txt" })),
                new ToolCallPending("call_1"),
                new ToolCallInProgress("call_1")),
            Buffer: TurnBuffer.Empty,
            Tools: ImmutableArray.Create(ToolSchemas.ReadTextFile));

        var finalResult = new { content = "File contents here", bytes = 1024 };
        var observed = new ObservedToolCallCompleted(
            ToolId: "call_1",
            Result: finalResult);

        // ACT
        var result = Core.Reduce(initial, observed);

        // ASSERT: Completion committed
        Assert.Contains(result.Next.Committed, evt =>
            evt is ToolCallCompleted completed &&
            completed.ToolId == "call_1" &&
            completed.Result.GetProperty("bytes").GetInt32() == 1024);

        // ASSERT: Newly committed contains exactly the completion event
        Assert.Single(result.NewlyCommitted);
        var committed = Assert.IsType<ToolCallCompleted>(result.NewlyCommitted[0]);
        Assert.Equal("call_1", committed.ToolId);
    }

    /// <summary>
    /// TC-CORE-006: Capability Absent → Tool Not Included in Catalog
    /// 
    /// WHY THIS IS AN INVARIANT:
    /// Tools requiring capabilities the client doesn't have must NEVER be exposed to the model.
    /// This prevents the model from requesting tools that cannot be executed, avoiding user confusion
    /// and ensuring the agent only offers capabilities it can actually deliver.
    /// </summary>
    [Fact]
    public void ToolCatalog_ExcludesTools_WhenCapabilityAbsent()
    {
        // ARRANGE: Client without filesystem read capability
        var capabilities = new Agent.Acp.Schema.ClientCapabilities
        {
            Fs = new Agent.Acp.Schema.FileSystemCapabilities
            {
                ReadTextFile = false,  // ← Capability missing
                WriteTextFile = true,
            },
            Terminal = false,
        };

        // ACT
        var catalog = Core.RenderToolCatalog(capabilities);

        // ASSERT: read_text_file NOT in catalog
        Assert.DoesNotContain(catalog, tool => tool.Name == "read_text_file");

        // ASSERT: write_text_file IS in catalog (capability present)
        Assert.Contains(catalog, tool => tool.Name == "write_text_file");

        // ASSERT: Capability-independent tools (if any) are included
        // (This depends on what tools are defined; adjust as needed)
    }

    /// <summary>
    /// TC-CORE-007: Tool Execution Failure Commits ToolCallFailed
    /// 
    /// WHY THIS IS AN INVARIANT:
    /// Failures must be observable in committed state without crashing the session. This enables
    /// graceful error handling, user feedback, and session recovery.
    /// </summary>
    [Fact]
    public void ToolExecutionFailed_Commits_ToolCallFailed()
    {
        // ARRANGE
        var initial = new SessionState(
            Committed: ImmutableArray.Create<SessionEvent>(
                new UserMessage("Read file"),
                new ToolCallRequested("call_1", "read_text_file", J(new { path = "/nonexistent.txt" })), 
                new ToolCallPending("call_1"),
                new ToolCallInProgress("call_1")),
            Buffer: TurnBuffer.Empty,
            Tools: ImmutableArray.Create(ToolSchemas.ReadTextFile));

        var observed = new ObservedToolCallFailed(
            ToolId: "call_1",
            Error: "File not found: /nonexistent.txt");

        // ACT
        var result = Core.Reduce(initial, observed);

        // ASSERT: Failure committed
        Assert.Contains(result.Next.Committed, evt =>
            evt is ToolCallFailed failed &&
            failed.ToolId == "call_1" &&
            failed.Error == "File not found: /nonexistent.txt");

        // ASSERT: No effects (failure is terminal)
        Assert.Empty(result.Effects);
    }

    /// <summary>
    /// TC-CORE-008: Tool Call Cancellation Commits ToolCallCancelled
    /// 
    /// WHY THIS IS AN INVARIANT:
    /// Cancellation must be observable and cleanly terminate the tool call without leaving it in
    /// an undefined state. This ensures deterministic state transitions even under cancellation.
    /// </summary>
    [Fact]
    public void ToolCallCancelled_Commits_ToolCallCancelled()
    {
        // ARRANGE
        var initial = new SessionState(
            Committed: ImmutableArray.Create<SessionEvent>(
                new UserMessage("Read large file"),
                new ToolCallRequested("call_1", "read_text_file", J(new { path = "/tmp/large.txt" })), 
                new ToolCallPending("call_1"),
                new ToolCallInProgress("call_1")),
            Buffer: TurnBuffer.Empty,
            Tools: ImmutableArray.Create(ToolSchemas.ReadTextFile));

        var observed = new ObservedToolCallCancelled(ToolId: "call_1");

        // ACT
        var result = Core.Reduce(initial, observed);

        // ASSERT: Cancellation committed
        Assert.Contains(result.Next.Committed, evt =>
            evt is ToolCallCancelled cancelled &&
            cancelled.ToolId == "call_1");

        // ASSERT: Terminal state, no further effects
        Assert.Empty(result.Effects);
    }
}

