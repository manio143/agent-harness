using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;

namespace Agent.Harness;

public sealed record CoreOptions(
    bool EmitModelInvokedEvents = false,
    bool CommitAssistantTextDeltas = false,
    bool CommitReasoningTextDeltas = false);

/// <summary>
/// Functional core reducer.
///
/// Rules (initial slice):
/// - ObservedUserMessage commits UserMessage.
/// - ObservedAssistantTextDelta appends to the in-flight assistant buffer.
/// - ObservedAssistantMessageCompleted flushes the assistant buffer into AssistantMessage.
/// - RenderPrompt uses ONLY committed user/assistant messages (debug events are ignored).
/// - RenderToolCatalog filters tools by client capabilities (capability gating).
/// </summary>
public static class Core
{
    private static bool HasTerminalToolCall(SessionState state, string toolId)
        => state.Committed.Any(e => e switch
        {
            ToolCallCompleted c when c.ToolId == toolId => true,
            ToolCallFailed f when f.ToolId == toolId => true,
            ToolCallCancelled c when c.ToolId == toolId => true,
            ToolCallRejected r when r.ToolId == toolId => true,
            _ => false,
        });

    /// <summary>
    /// Render the tool catalog based on client capabilities.
    /// Tools requiring unavailable capabilities are filtered out.
    /// </summary>
    public static ImmutableArray<ToolDefinition> RenderToolCatalog(Agent.Acp.Schema.ClientCapabilities capabilities)
    {
        var builder = ImmutableArray.CreateBuilder<ToolDefinition>();

        // Filesystem tools
        if (capabilities.Fs?.ReadTextFile == true)
        {
            builder.Add(ToolSchemas.ReadTextFile);

        }

        if (capabilities.Fs?.WriteTextFile == true)
        {
            builder.Add(ToolSchemas.WriteTextFile);

        }

        // Terminal tools
        if (capabilities.Terminal == true)
        {
            builder.Add(ToolSchemas.ExecuteCommand);

        }

        return builder.ToImmutable();
    }
    public static ReduceResult Reduce(SessionState state, ObservedChatEvent evt, CoreOptions? options = null)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        if (evt is null) throw new ArgumentNullException(nameof(evt));

        switch (evt)
        {
            case ObservedTurnStarted:
                return Commit(state, new TurnStarted());

            case ObservedUserMessage m:
            {
                // Commit the message AND request a model call.
                var msgEvt = new UserMessage(m.Text);
                var committedMsg = state.Committed.Add(msgEvt);
                var next = state with { Committed = committedMsg };
                return new ReduceResult(
                    next,
                    ImmutableArray.Create<SessionEvent>(msgEvt),
                    ImmutableArray.Create<Effect>(new CallModel()));
            }

            case ObservedAssistantTextDelta d:
            {
                var next = state with
                {
                    Buffer = state.Buffer with
                    {
                        AssistantMessageOpen = true,
                        AssistantText = state.Buffer.AssistantText + d.Text,
                    },
                };

                if (options?.CommitAssistantTextDeltas == true)
                {
                    var delta = new AssistantTextDelta(d.Text);
                    var committed = next.Committed.Add(delta);
                    return new ReduceResult(
                        next with { Committed = committed },
                        ImmutableArray.Create<SessionEvent>(delta),
                        ImmutableArray<Effect>.Empty);
                }

                return new ReduceResult(next, ImmutableArray<SessionEvent>.Empty, ImmutableArray<Effect>.Empty);
            }

            case ObservedReasoningTextDelta d:
            {
                if (options?.CommitReasoningTextDeltas == true)
                {
                    var delta = new ReasoningTextDelta(d.Text);
                    var committed = state.Committed.Add(delta);
                    return new ReduceResult(
                        state with { Committed = committed },
                        ImmutableArray.Create<SessionEvent>(delta),
                        ImmutableArray<Effect>.Empty);
                }

                return new ReduceResult(state, ImmutableArray<SessionEvent>.Empty, ImmutableArray<Effect>.Empty);
            }

            case ObservedAssistantMessageCompleted:
                return FlushAssistant(state);

            case ObservedTurnStabilized:
                return Commit(state, new TurnEnded());

            // --- Tool Call Lifecycle Observations ---

            case ObservedToolCallDetected detected:
            {
                // Deduplicate: some model/provider streams may repeat tool call content.
                // Invariant: a given toolId must be requested at most once.
                if (state.Committed.Any(e => e is ToolCallRequested r && r.ToolId == detected.ToolId))
                    return new ReduceResult(state, ImmutableArray<SessionEvent>.Empty, ImmutableArray<Effect>.Empty);

                // Early rejection: unknown tool / invalid args
                var tool = state.Tools.FirstOrDefault(t => t.Name == detected.ToolName);
                if (tool is null)
                {
                    var rejected = new ToolCallRejected(detected.ToolId, "unknown_tool", ImmutableArray.Create("unknown_tool"));
                    var committedRej = state.Committed.Add(rejected);
                    var nextRej = state with { Committed = committedRej };
                    return new ReduceResult(
                        nextRej,
                        ImmutableArray.Create<SessionEvent>(rejected),
                        ImmutableArray.Create<Effect>(new CallModel()));
                }

                var errors = ToolArgValidator.Validate(tool.InputSchema, detected.Args);
                if (!errors.IsEmpty)
                {
                    var rejected = new ToolCallRejected(detected.ToolId, "invalid_args", errors);
                    var committedRej = state.Committed.Add(rejected);
                    var nextRej = state with { Committed = committedRej };
                    return new ReduceResult(
                        nextRej,
                        ImmutableArray.Create<SessionEvent>(rejected),
                        ImmutableArray.Create<Effect>(new CallModel()));
                }

                // Commit ToolCallRequested and emit CheckPermission effect
                var argsJson = JsonSerializer.SerializeToElement(detected.Args);
                var requested = new ToolCallRequested(detected.ToolId, detected.ToolName, argsJson);
                var permissionEffect = new CheckPermission(detected.ToolId, detected.ToolName, detected.Args);

                var committed = state.Committed.Add(requested);
                var next = state with { Committed = committed };

                return new ReduceResult(
                    next,
                    ImmutableArray.Create<SessionEvent>(requested),
                    ImmutableArray.Create<Effect>(permissionEffect));
            }

            case ObservedPermissionApproved approved:
            {
                // Commit ToolCallPermissionApproved + ToolCallPending and emit ExecuteToolCall effect
                var approvedEvt = new ToolCallPermissionApproved(approved.ToolId, approved.Reason);
                var pending = new ToolCallPending(approved.ToolId);

                var committed = state.Committed.Add(approvedEvt).Add(pending);

                // Find the original ToolCallRequested to get tool name and args
                var requestedEvent = state.Committed
                    .OfType<ToolCallRequested>()
                    .FirstOrDefault(r => r.ToolId == approved.ToolId);

                if (requestedEvent is null)
                {
                    // Should not happen in well-formed sessions
                    return new ReduceResult(state, ImmutableArray<SessionEvent>.Empty, ImmutableArray<Effect>.Empty);
                }

                var executeEffect = new ExecuteToolCall(
                    approved.ToolId,
                    requestedEvent.ToolName,
                    requestedEvent.Args);

                var next = state with { Committed = committed };
                return new ReduceResult(
                    next,
                    ImmutableArray.Create<SessionEvent>(approvedEvt, pending),
                    ImmutableArray.Create<Effect>(executeEffect));
            }

            case ObservedPermissionDenied denied:
            {
                // Commit ToolCallPermissionDenied + ToolCallRejected with no execution, then request a model call.
                var deniedEvt = new ToolCallPermissionDenied(denied.ToolId, denied.Reason);
                var rejected = new ToolCallRejected(denied.ToolId, denied.Reason, ImmutableArray<string>.Empty);
                var committed = state.Committed.Add(deniedEvt).Add(rejected);
                var next = state with { Committed = committed };

                return new ReduceResult(
                    next,
                    ImmutableArray.Create<SessionEvent>(deniedEvt, rejected),
                    ImmutableArray.Create<Effect>(new CallModel()));
            }

            case ObservedToolCallProgressUpdate progress:
            {
                var committed = state.Committed;
                
                // Check if we need to transition from Pending to InProgress
                var hasInProgress = state.Committed
                    .OfType<ToolCallInProgress>()
                    .Any(ip => ip.ToolId == progress.ToolId);
                
                if (!hasInProgress)
                {
                    // First progress update - transition to InProgress first
                    var inProgress = new ToolCallInProgress(progress.ToolId);
                    committed = committed.Add(inProgress);
                }
                
                // Commit ToolCallUpdate for incremental updates
                var update = new ToolCallUpdate(progress.ToolId, JsonSerializer.SerializeToElement(progress.Content));
                committed = committed.Add(update);
                var next = state with { Committed = committed };
                
                // NewlyCommitted might include InProgress + Update
                var newlyCommitted = !hasInProgress
                    ? ImmutableArray.Create<SessionEvent>(new ToolCallInProgress(progress.ToolId), update)
                    : ImmutableArray.Create<SessionEvent>(update);
                
                return new ReduceResult(
                    next,
                    newlyCommitted,
                    ImmutableArray<Effect>.Empty);
            }

            case ObservedToolCallCompleted completed:
            {
                // Idempotency: if a terminal event already exists for this toolId, ignore duplicates.
                if (HasTerminalToolCall(state, completed.ToolId))
                    return new ReduceResult(state, ImmutableArray<SessionEvent>.Empty, ImmutableArray<Effect>.Empty);

                // Commit ToolCallCompleted (terminal state) then request a model call.
                var completedEvent = new ToolCallCompleted(completed.ToolId, JsonSerializer.SerializeToElement(completed.Result));
                var committed = state.Committed.Add(completedEvent);
                var next = state with { Committed = committed };

                return new ReduceResult(
                    next,
                    ImmutableArray.Create<SessionEvent>(completedEvent),
                    ImmutableArray.Create<Effect>(new CallModel()));
            }

            case ObservedToolCallFailed failed:
            {
                if (HasTerminalToolCall(state, failed.ToolId))
                    return new ReduceResult(state, ImmutableArray<SessionEvent>.Empty, ImmutableArray<Effect>.Empty);

                // Commit ToolCallFailed (terminal state) then request a model call.
                var failedEvent = new ToolCallFailed(failed.ToolId, failed.Error);
                var committed = state.Committed.Add(failedEvent);
                var next = state with { Committed = committed };

                return new ReduceResult(
                    next,
                    ImmutableArray.Create<SessionEvent>(failedEvent),
                    ImmutableArray.Create<Effect>(new CallModel()));
            }

            case ObservedToolCallCancelled cancelled:
            {
                if (HasTerminalToolCall(state, cancelled.ToolId))
                    return new ReduceResult(state, ImmutableArray<SessionEvent>.Empty, ImmutableArray<Effect>.Empty);

                // Commit ToolCallCancelled (terminal state) then request a model call.
                var cancelledEvent = new ToolCallCancelled(cancelled.ToolId);
                var committed = state.Committed.Add(cancelledEvent);
                var next = state with { Committed = committed };

                return new ReduceResult(
                    next,
                    ImmutableArray.Create<SessionEvent>(cancelledEvent),
                    ImmutableArray.Create<Effect>(new CallModel()));
            }

            default:
                return new ReduceResult(state, ImmutableArray<SessionEvent>.Empty, ImmutableArray<Effect>.Empty);
        }
    }

    public static ImmutableArray<ChatMessage> RenderPrompt(SessionState state)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));

        var builder = ImmutableArray.CreateBuilder<ChatMessage>();
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        foreach (var evt in state.Committed)
        {
            switch (evt)
            {
                case UserMessage u:
                    builder.Add(new ChatMessage(ChatRole.User, u.Text));
                    break;

                case AssistantMessage a:
                    builder.Add(new ChatMessage(ChatRole.Assistant, a.Text));
                    break;

                case ToolCallRequested t:
                {
                    var payload = JsonSerializer.Serialize(new { toolId = t.ToolId, toolName = t.ToolName, args = t.Args }, json);
                    builder.Add(new ChatMessage(ChatRole.System, $"<tool_call>{payload}</tool_call>"));
                    break;
                }

                case ToolCallUpdate u:
                {
                    var payload = JsonSerializer.Serialize(new { toolId = u.ToolId, content = u.Content }, json);
                    builder.Add(new ChatMessage(ChatRole.System, $"<tool_update>{payload}</tool_update>"));
                    break;
                }

                case ToolCallCompleted c:
                {
                    var payload = JsonSerializer.Serialize(new { toolId = c.ToolId, outcome = "completed", result = c.Result }, json);
                    builder.Add(new ChatMessage(ChatRole.System, $"<tool_result>{payload}</tool_result>"));
                    break;
                }

                case ToolCallFailed f:
                {
                    var payload = JsonSerializer.Serialize(new { toolId = f.ToolId, outcome = "failed", error = f.Error }, json);
                    builder.Add(new ChatMessage(ChatRole.System, $"<tool_result>{payload}</tool_result>"));
                    break;
                }

                case ToolCallRejected r:
                {
                    var payload = JsonSerializer.Serialize(new { toolId = r.ToolId, outcome = "rejected", reason = r.Reason, details = r.Details }, json);
                    builder.Add(new ChatMessage(ChatRole.System, $"<tool_result>{payload}</tool_result>"));
                    break;
                }

                case ToolCallCancelled c:
                {
                    var payload = JsonSerializer.Serialize(new { toolId = c.ToolId, outcome = "cancelled" }, json);
                    builder.Add(new ChatMessage(ChatRole.System, $"<tool_result>{payload}</tool_result>"));
                    break;
                }

                default:
                    // Ignore other committed events for prompt rendering.
                    break;
            }
        }

        return builder.ToImmutable();
    }

    private static ReduceResult Commit(SessionState state, SessionEvent evt)
    {
        var committed = state.Committed.Add(evt);
        var next = state with { Committed = committed };
        return new ReduceResult(next, ImmutableArray.Create(evt), ImmutableArray<Effect>.Empty);
    }

    private static ReduceResult FlushAssistant(SessionState state)
    {
        if (!state.Buffer.AssistantMessageOpen && string.IsNullOrEmpty(state.Buffer.AssistantText))
        {
            // Nothing to flush.
            return new ReduceResult(state, ImmutableArray<SessionEvent>.Empty, ImmutableArray<Effect>.Empty);
        }

        var text = state.Buffer.AssistantText;
        var nextState = state with { Buffer = TurnBuffer.Empty };

        // Commit even if text is empty: treat boundary as a message completion.
        // (We can tighten this later if desired.)
        return Commit(nextState, new AssistantMessage(text));
    }
}
