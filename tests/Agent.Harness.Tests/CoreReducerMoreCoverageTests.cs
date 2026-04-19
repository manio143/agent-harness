using System.Collections.Immutable;
using Agent.Acp.Schema;
using Agent.Harness;
using Agent.Harness.Threads;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class CoreReducerMoreCoverageTests
{
    [Fact]
    public void RenderToolCatalog_WhenCapabilitiesAdvertiseFsAndTerminal_IncludesExpectedTools()
    {
        var caps = new ClientCapabilities
        {
            Fs = new FileSystemCapabilities { ReadTextFile = true, WriteTextFile = true },
            Terminal = true,
        };

        var tools = Core.RenderToolCatalog(caps);

        var names = tools.Select(t => t.Name).ToArray();

        names.Should().Contain(new[]
        {
            ToolSchemas.ReadTextFile.Name,
            ToolSchemas.WriteTextFile.Name,
            ToolSchemas.PatchTextFile.Name,
            ToolSchemas.ExecuteCommand.Name,
        });

        // No accidental duplicates in catalog.
        names.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Reduce_WhenPermissionApprovedButNoToolCallRequested_IsNoOp()
    {
        var state = SessionState.Empty;

        var result = Core.Reduce(state, new ObservedPermissionApproved("t1", "ok"));

        result.Next.Should().Be(state);
        result.NewlyCommitted.Should().BeEmpty();
        result.Effects.Should().BeEmpty();
    }

    [Fact]
    public void Reduce_WhenMcpConnectionFailed_IsIgnoredByDefaultBranch()
    {
        var state = SessionState.Empty;

        var result = Core.Reduce(state, new ObservedMcpConnectionFailed("srv", "boom"));

        result.Next.Should().Be(state);
        result.NewlyCommitted.Should().BeEmpty();
        result.Effects.Should().BeEmpty();
    }

    [Fact]
    public void Reduce_WhenToolAlreadyTerminalRejected_IgnoresDuplicateTerminalObservation()
    {
        var state = SessionState.Empty with
        {
            Committed = ImmutableArray.Create<SessionEvent>(
                new ToolCallRejected("call_0", "missing_report_intent", ImmutableArray.Create("x")))
        };

        var result = Core.Reduce(state, new ObservedToolCallFailed("call_0", "failed"));

        result.Next.Should().Be(state);
        result.NewlyCommitted.Should().BeEmpty();
        result.Effects.Should().BeEmpty();
    }

    [Fact]
    public void RenderPrompt_WhenToolCallCancelled_RendersToolOutcomeCancelledPayload()
    {
        var state = SessionState.Empty with
        {
            Committed = ImmutableArray.Create<SessionEvent>(new ToolCallCancelled("call_0")),
        };

        var prompt = Core.RenderPrompt(state);

        var toolMsg = prompt.Should().ContainSingle(m => m.Role == ChatRole.Tool).Subject;
        toolMsg.Text.Should().Contain("\"outcome\":\"cancelled\"");
        toolMsg.Text.Should().Contain("\"toolId\":\"call_0\"");
    }

    [Fact]
    public void Reduce_WakeModel_WhenInboxKindUnknown_PromotesAsInterThreadMessageFallback()
    {
        var arrived = ThreadInboxArrivals.InterThreadMessage(
            threadId: ThreadIds.Main,
            text: "hi",
            source: "child",
            sourceThreadId: "thr_child",
            delivery: InboxDelivery.Immediate);

        // Force an unknown kind value to exercise the reducer fallback branch.
        var unknownKind = (ThreadInboxMessageKind)999;

        var state = SessionState.Empty with
        {
            Committed = ImmutableArray.Create<SessionEvent>(
                new TurnStarted(),
                new ThreadInboxMessageEnqueued(
                    ThreadId: arrived.ThreadId,
                    EnvelopeId: arrived.EnvelopeId,
                    Kind: unknownKind,
                    Meta: arrived.Meta,
                    Source: arrived.Source,
                    SourceThreadId: arrived.SourceThreadId,
                    Delivery: arrived.Delivery.ToString().ToLowerInvariant(),
                    EnqueuedAtIso: arrived.EnqueuedAtIso,
                    Text: arrived.Text))
        };

        var result = Core.Reduce(state, new ObservedWakeModel(ThreadIds.Main));

        result.NewlyCommitted.Should().ContainSingle(e => e is InterThreadMessage);
        result.NewlyCommitted.OfType<InterThreadMessage>().Single().FromThreadId.Should().Be("thr_child");
    }

    [Fact]
    public void Reduce_WhenReasoningMessageCompletedButNothingOpen_IsNoOp()
    {
        var state = SessionState.Empty;

        var result = Core.Reduce(state, new ObservedReasoningMessageCompleted(), options: new CoreOptions(CommitReasoningTextDeltas: true));

        result.NewlyCommitted.Should().BeEmpty();
        result.Effects.Should().BeEmpty();
    }
}
