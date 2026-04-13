using System.Collections.Immutable;
using Agent.Acp.Acp;
using Agent.Acp.Schema;
using Agent.Harness;

namespace Agent.Harness.Tests;

/// <summary>
/// Tests for SessionRunner orchestration of tool call effects.
/// 
/// Invariant: SessionRunner is the imperative shell that executes Effects emitted by the reducer.
/// It translates effects into actual I/O (ACP requests, filesystem ops, MCP calls) and feeds
/// observations back to the reducer.
/// </summary>
public class ToolCallSessionRunnerTests
{
    /// <summary>
    /// TC-RUNNER-001: SessionRunner Executes CheckPermission Effect
    /// 
    /// WHY THIS IS AN INVARIANT:
    /// The SessionRunner must translate emitted CheckPermission effects into actual ACP
    /// session/request_permission calls. This is the boundary between pure reducer logic
    /// and impure I/O.
    /// </summary>
    [Fact]
    public async Task SessionRunner_Executes_CheckPermission_Effect_Via_AcpClient()
    {
        // ARRANGE: Fake ACP client that simulates permission approval
        var permissionRequests = new List<(string toolId, string toolName)>();
        var fakeClient = new FakeAcpClientCaller(
            caps: new ClientCapabilities
            {
                Fs = new FileSystemCapabilities { ReadTextFile = true, WriteTextFile = true },
            },
            onRequestPermission: (req) =>
            {
                permissionRequests.Add((req.ToolCall!.ToolCallId!, "read_text_file"));
                return Task.FromResult(new RequestPermissionResponse
                {
                    Outcome = new RequestPermissionOutcome
                    {
                        Outcome = RequestPermissionOutcomeOutcome.Selected,
                        OptionId = "allow-once",
                    },
                });
            });

        // SessionRunner setup (placeholder: actual impl will vary)
        var runner = new ToolCallSessionRunner(fakeClient);

        var effect = new CheckPermission(
            ToolId: "call_1",
            ToolName: "read_text_file",
            Args: new { path = "/tmp/test.txt" });

        // ACT: Execute the effect
        var observations = await runner.ExecuteEffectAsync(effect, CancellationToken.None);

        // ASSERT: Permission was requested via ACP client
        Assert.Single(permissionRequests);
        Assert.Equal("call_1", permissionRequests[0].toolId);

        // ASSERT: Observation fed back is ObservedPermissionApproved
        var approvedObs = Assert.Single(observations.OfType<ObservedPermissionApproved>());
        Assert.Equal("call_1", approvedObs.ToolId);
    }

    /// <summary>
    /// TC-RUNNER-002: SessionRunner Executes ExecuteToolCall Effect (Filesystem Example)
    /// 
    /// WHY THIS IS AN INVARIANT:
    /// SessionRunner must route tool execution to the appropriate handler (filesystem, terminal, MCP).
    /// It must stream progress observations back to the reducer, maintaining the unidirectional
    /// data flow: Effect → Execute → Observations → Reducer.
    /// </summary>
    [Fact]
    public async Task SessionRunner_Executes_FilesystemToolCall_And_Streams_Observations()
    {
        // ARRANGE: Fake filesystem executor
        var fakeClient = new FakeAcpClientCaller(
            caps: new ClientCapabilities
            {
                Fs = new FileSystemCapabilities { ReadTextFile = true, WriteTextFile = true },
            });

        var runner = new ToolCallSessionRunner(fakeClient);

        var effect = new ExecuteToolCall(
            ToolId: "call_1",
            ToolName: "read_text_file",
            Args: new { path = "/tmp/test.txt" });

        // ACT
        var observations = await runner.ExecuteEffectAsync(effect, CancellationToken.None);

        // ASSERT: Observations include InProgress, Progress, and Completed
        Assert.Contains(observations, obs => obs is ObservedToolCallProgressUpdate);
        Assert.Contains(observations, obs =>
            obs is ObservedToolCallCompleted completed && completed.ToolId == "call_1");

        // ASSERT: Final result contains file content (mocked)
        var completedObs = observations.OfType<ObservedToolCallCompleted>().Single();
        Assert.NotNull(completedObs.Result);
    }

    /// <summary>
    /// TC-RUNNER-003: SessionRunner Handles Tool Execution Failure
    /// 
    /// WHY THIS IS AN INVARIANT:
    /// Failures must be observable and committed without crashing the session. The SessionRunner
    /// must catch exceptions from tool executors and translate them into ObservedToolCallFailed
    /// observations.
    /// </summary>
    [Fact]
    public async Task SessionRunner_Handles_FilesystemToolCall_Failure_Gracefully()
    {
        // ARRANGE: Filesystem executor that will fail (nonexistent file)
        var fakeClient = new FakeAcpClientCaller(
            caps: new ClientCapabilities
            {
                Fs = new FileSystemCapabilities { ReadTextFile = true, WriteTextFile = true },
            });

        var runner = new ToolCallSessionRunner(fakeClient);

        var effect = new ExecuteToolCall(
            ToolId: "call_1",
            ToolName: "read_text_file",
            Args: new { path = "/nonexistent/file.txt" });

        // ACT
        var observations = await runner.ExecuteEffectAsync(effect, CancellationToken.None);

        // ASSERT: Failure observation emitted
        var failedObs = Assert.Single(observations.OfType<ObservedToolCallFailed>());
        Assert.Equal("call_1", failedObs.ToolId);
        Assert.Contains("not found", failedObs.Error, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// TC-RUNNER-004: SessionRunner Respects Turn Cancellation
    /// 
    /// WHY THIS IS AN INVARIANT:
    /// Cancellation must propagate to in-flight tool calls and emit proper terminal states.
    /// This ensures sessions can be cleanly aborted without leaving resources hanging.
    /// </summary>
    [Fact]
    public async Task SessionRunner_Propagates_Cancellation_To_ToolExecution()
    {
        // ARRANGE
        var fakeClient = new FakeAcpClientCaller(
            caps: new ClientCapabilities
            {
                Fs = new FileSystemCapabilities { ReadTextFile = true, WriteTextFile = true },
            });

        var runner = new ToolCallSessionRunner(fakeClient);

        var effect = new ExecuteToolCall(
            ToolId: "call_1",
            ToolName: "read_text_file",
            Args: new { path = "/tmp/large-file.txt" });

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(10)); // Cancel almost immediately

        // ACT
        var observations = await runner.ExecuteEffectAsync(effect, cts.Token);

        // ASSERT: Cancellation observation emitted
        var cancelledObs = Assert.Single(observations.OfType<ObservedToolCallCancelled>());
        Assert.Equal("call_1", cancelledObs.ToolId);
    }
}

/// <summary>
/// Placeholder SessionRunner for tool calls.
/// (Actual implementation will integrate with real SessionRunner.)
/// </summary>
internal class ToolCallSessionRunner
{
    private readonly IAcpClientCaller _client;

    public ToolCallSessionRunner(IAcpClientCaller client)
    {
        _client = client;
    }

    public async Task<ImmutableArray<ObservedChatEvent>> ExecuteEffectAsync(
        Effect effect,
        CancellationToken cancellationToken)
    {
        // RED: Not implemented yet
        // This is where the implementation driver will:
        // 1. Pattern match on Effect type
        // 2. Execute the appropriate side effect
        // 3. Return observations
        
        await Task.CompletedTask; // Suppress async warning
        throw new NotImplementedException(
            $"ToolCallSessionRunner.ExecuteEffectAsync not implemented for effect: {effect}");
    }
}

/// <summary>
/// Fake ACP client caller for tests (no real JSON-RPC transport).
/// </summary>
internal class FakeAcpClientCaller : IAcpClientCaller
{
    private readonly Func<RequestPermissionRequest, Task<RequestPermissionResponse>>? _onRequestPermission;

    public FakeAcpClientCaller(
        ClientCapabilities caps,
        Func<RequestPermissionRequest, Task<RequestPermissionResponse>>? onRequestPermission = null)
    {
        ClientCapabilities = caps;
        _onRequestPermission = onRequestPermission;
    }

    public ClientCapabilities ClientCapabilities { get; }

    public async Task<TResponse> RequestAsync<TRequest, TResponse>(
        string method,
        TRequest request,
        CancellationToken cancellationToken = default)
    {
        if (method == "session/request_permission" && request is RequestPermissionRequest permReq)
        {
            if (_onRequestPermission is not null)
            {
                var response = await _onRequestPermission(permReq);
                return (TResponse)(object)response;
            }
        }

        throw new NotSupportedException($"FakeAcpClientCaller: unsupported method {method}");
    }
}
