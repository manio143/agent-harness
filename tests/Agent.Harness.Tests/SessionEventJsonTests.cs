using Agent.Harness.Persistence;
using Agent.Harness.Threads;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class SessionEventJsonTests
{
    [Fact]
    public void Deserialize_ThreadInboxMessageEnqueued_WhenKindUnknown_PreservesRawKindInMeta()
    {
        var json = """
        {"type":"thread_inbox_message_enqueued","threadId":"main","envelopeId":"env1","kind":"FutureKind","meta":null,"source":"thread","sourceThreadId":"thr_x","delivery":"immediate","enqueuedAtIso":"t","text":"hi"}
        """;

        var evt = SessionEventJson.Deserialize(json);

        var enq = evt.Should().BeOfType<ThreadInboxMessageEnqueued>().Subject;
        enq.Kind.Should().Be(ThreadInboxMessageKind.InterThreadMessage);
        enq.Meta.Should().NotBeNull();
        enq.Meta!.Should().ContainKey(ThreadInboxMetaKeys.UnknownInboxKind).WhoseValue.Should().Be("FutureKind");
    }

    [Fact]
    public void Deserialize_ThreadInboxMessageEnqueued_WhenKindUnknown_AndMetaPresent_DoesNotDropMeta()
    {
        var json = """
        {"type":"thread_inbox_message_enqueued","threadId":"main","envelopeId":"env1","kind":"FutureKind","meta":{"k":"v"},"source":"thread","sourceThreadId":"thr_x","delivery":"immediate","enqueuedAtIso":"t","text":"hi"}
        """;

        var evt = SessionEventJson.Deserialize(json);

        var enq = evt.Should().BeOfType<ThreadInboxMessageEnqueued>().Subject;
        enq.Meta!.Should().ContainKey("k").WhoseValue.Should().Be("v");
        enq.Meta!.Should().ContainKey(ThreadInboxMetaKeys.UnknownInboxKind).WhoseValue.Should().Be("FutureKind");
    }

    [Fact]
    public void Deserialize_ThreadInboxMessageEnqueued_WhenKindIsNumeric_DecodesEnumValue()
    {
        var json = """
        {"type":"thread_inbox_message_enqueued","threadId":"main","envelopeId":"env1","kind":3,"meta":null,"source":"thread","sourceThreadId":"thr_x","delivery":"immediate","enqueuedAtIso":"t","text":"hi"}
        """;

        var evt = SessionEventJson.Deserialize(json);

        var enq = evt.Should().BeOfType<ThreadInboxMessageEnqueued>().Subject;
        enq.Kind.Should().Be(ThreadInboxMessageKind.NewThreadTask);
        enq.Meta.Should().BeNull();
    }

    [Fact]
    public void Deserialize_ThreadInboxMessageEnqueued_WhenKindIsUnknownNumeric_PreservesRawKindInMeta()
    {
        var json = """
        {"type":"thread_inbox_message_enqueued","threadId":"main","envelopeId":"env1","kind":99,"meta":null,"source":"thread","sourceThreadId":"thr_x","delivery":"immediate","enqueuedAtIso":"t","text":"hi"}
        """;

        var evt = SessionEventJson.Deserialize(json);

        var enq = evt.Should().BeOfType<ThreadInboxMessageEnqueued>().Subject;
        enq.Kind.Should().Be(ThreadInboxMessageKind.InterThreadMessage);
        enq.Meta!.Should().ContainKey(ThreadInboxMetaKeys.UnknownInboxKind).WhoseValue.Should().Be("99");
    }
}
