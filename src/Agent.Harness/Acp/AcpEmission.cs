using System.Collections.Immutable;

namespace Agent.Harness.Acp;

/// <summary>
/// Functional projection output for ACP publishing.
///
/// These are imperative-shell instructions ("effects") for the ACP boundary.
/// They are NOT the durable session log.
/// </summary>
public abstract record AcpEmission;

public sealed record AcpSendAgentMessageChunk(string Text) : AcpEmission;

public sealed record AcpSendAgentThoughtChunk(string Text) : AcpEmission;

public sealed record AcpToolCallStart(string ToolId, string Title) : AcpEmission;

public sealed record AcpToolCallInProgress(string ToolId) : AcpEmission;

public sealed record AcpToolCallAddText(string ToolId, string Text) : AcpEmission;

public sealed record AcpToolCallCompleted(string ToolId) : AcpEmission;

public sealed record AcpToolCallFailed(string ToolId, string Message) : AcpEmission;

public sealed record AcpToolCallCancelled(string ToolId) : AcpEmission;

public static class AcpProjection
{
    public static ImmutableArray<AcpEmission> Project(SessionEvent committed, CoreOptions coreOptions, AcpPublishOptions publishOptions)
    {
        switch (committed)
        {
            case AssistantMessage a:
                if (!coreOptions.CommitAssistantTextDeltas)
                    return ImmutableArray.Create<AcpEmission>(new AcpSendAgentMessageChunk(a.Text));
                return ImmutableArray<AcpEmission>.Empty;

            case AssistantTextDelta d:
                return ImmutableArray.Create<AcpEmission>(new AcpSendAgentMessageChunk(d.TextDelta));

            case ReasoningMessage r when publishOptions.PublishReasoning:
                if (!coreOptions.CommitReasoningTextDeltas)
                    return ImmutableArray.Create<AcpEmission>(new AcpSendAgentThoughtChunk(r.Text));
                return ImmutableArray<AcpEmission>.Empty;

            case ReasoningTextDelta r when publishOptions.PublishReasoning:
                return ImmutableArray.Create<AcpEmission>(new AcpSendAgentThoughtChunk(r.TextDelta));

            case ToolCallRequested req:
                return ImmutableArray.Create<AcpEmission>(new AcpToolCallStart(req.ToolId, req.ToolName));

            case ToolCallInProgress ip:
                return ImmutableArray.Create<AcpEmission>(new AcpToolCallInProgress(ip.ToolId));

            case ToolCallUpdate u:
            {
                var text = u.Content.ValueKind == System.Text.Json.JsonValueKind.String
                    ? u.Content.GetString() ?? string.Empty
                    : u.Content.GetRawText();

                return ImmutableArray.Create<AcpEmission>(new AcpToolCallAddText(u.ToolId, text));
            }

            case ToolCallCompleted done:
                return ImmutableArray.Create<AcpEmission>(new AcpToolCallCompleted(done.ToolId));

            case ToolCallFailed failed:
                return ImmutableArray.Create<AcpEmission>(new AcpToolCallFailed(failed.ToolId, failed.Error));

            case ToolCallCancelled cancelled:
                return ImmutableArray.Create<AcpEmission>(new AcpToolCallCancelled(cancelled.ToolId));

            case ToolCallRejected rejected:
            {
                var msg = rejected.Details.IsEmpty
                    ? rejected.Reason
                    : $"{rejected.Reason}: {string.Join(",", rejected.Details)}";

                // Keep presentation aligned with previous behavior: start a placeholder tool call titled "rejected"
                // and mark it as failed.
                return ImmutableArray.Create<AcpEmission>(
                    new AcpToolCallStart(rejected.ToolId, "rejected"),
                    new AcpToolCallFailed(rejected.ToolId, msg));
            }

            // Turn markers and other internal state are not projected to ACP.
            case TurnStarted:
            case TurnEnded:
            case UserMessage:
            case SessionTitleSet:
            case ModelInvoked:
            case ToolCallPermissionApproved:
            case ToolCallPermissionDenied:
            case ToolCallPending:
                return ImmutableArray<AcpEmission>.Empty;

            default:
                return ImmutableArray<AcpEmission>.Empty;
        }
    }
}
