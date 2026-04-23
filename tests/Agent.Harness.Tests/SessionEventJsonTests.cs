using Agent.Harness.Persistence;
using Agent.Harness.Threads;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class SessionEventJsonTests
{
    [Fact]
    public void SerializeDeserialize_TokenUsage_WithProviderModel_RoundTrips()
    {
        var evt = new TokenUsage(1, 2, 3, "qwen2.5:3b");

        var json = SessionEventJson.Serialize(evt);
        var decoded = SessionEventJson.Deserialize(json);

        decoded.Should().Be(evt);
    }

    [Fact]
    public void Deserialize_TokenUsage_WhenProviderModelMissing_DefaultsToNull()
    {
        var json = """
        {"type":"token_usage","inputTokens":1,"outputTokens":2,"totalTokens":3}
        """;

        var decoded = SessionEventJson.Deserialize(json);

        var usage = decoded.Should().BeOfType<TokenUsage>().Subject;
        usage.ProviderModel.Should().BeNull();
    }

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

    [Fact]
    public void Deserialize_ThreadInboxMessageEnqueued_WhenDeliveryMissing_DefaultsToImmediate()
    {
        var json = """
        {"type":"thread_inbox_message_enqueued","threadId":"main","envelopeId":"env1","kind":"InterThreadMessage","meta":null,"source":"thread","sourceThreadId":"thr_x","enqueuedAtIso":"t","text":"hi"}
        """;

        var evt = SessionEventJson.Deserialize(json);

        var enq = evt.Should().BeOfType<ThreadInboxMessageEnqueued>().Subject;
        enq.Delivery.Should().Be(ThreadInboxDeliveryText.Immediate);
    }

    [Fact]
    public void Deserialize_ThreadInboxMessageEnqueued_WhenEnqueuedAtMissing_DoesNotThrow()
    {
        var json = """
        {"type":"thread_inbox_message_enqueued","threadId":"main","envelopeId":"env1","kind":"InterThreadMessage","meta":null,"source":"thread","sourceThreadId":"thr_x","delivery":"immediate","text":"hi"}
        """;

        var evt = SessionEventJson.Deserialize(json);

        var enq = evt.Should().BeOfType<ThreadInboxMessageEnqueued>().Subject;
        enq.EnqueuedAtIso.Should().Be("");
    }

    [Fact]
    public void Deserialize_ThreadInboxMessageDequeued_WhenTimestampMissing_DoesNotThrow()
    {
        var json = """
        {"type":"thread_inbox_message_dequeued","threadId":"main","envelopeId":"env1"}
        """;

        var evt = SessionEventJson.Deserialize(json);

        var deq = evt.Should().BeOfType<ThreadInboxMessageDequeued>().Subject;
        deq.DequeuedAtIso.Should().Be("");
    }

    [Fact]
    public void Deserialize_ThreadInboxMessageDequeued_WhenThreadIdMissing_DoesNotThrow()
    {
        var json = """
        {"type":"thread_inbox_message_dequeued","envelopeId":"env1","dequeuedAtIso":"t"}
        """;

        var evt = SessionEventJson.Deserialize(json);

        var deq = evt.Should().BeOfType<ThreadInboxMessageDequeued>().Subject;
        deq.ThreadId.Should().Be("");
        deq.EnvelopeId.Should().Be("env1");
    }

    [Fact]
    public void Deserialize_ThreadInboxMessageDrainedForPrompt_WhenTimestampMissing_DoesNotThrow()
    {
        var json = """
        {"type":"thread_inbox_message_drained_for_prompt","threadId":"main","envelopeId":"env1"}
        """;

        var evt = SessionEventJson.Deserialize(json);

        var deq = evt.Should().BeOfType<ThreadInboxMessageDequeued>().Subject;
        deq.DequeuedAtIso.Should().Be("");
    }

    [Fact]
    public void Deserialize_ThreadInboxMessageDeliveredToLlm_WhenTimestampMissing_DoesNotThrow()
    {
        var json = """
        {"type":"thread_inbox_message_delivered_to_llm","threadId":"main","envelopeId":"env1"}
        """;

        var evt = SessionEventJson.Deserialize(json);

        var deq = evt.Should().BeOfType<ThreadInboxMessageDequeued>().Subject;
        deq.DequeuedAtIso.Should().Be("");
    }

    [Fact]
    public void SerializeDeserialize_ThreadCompacted_RoundTrips()
    {
        var evt = new ThreadCompacted("<compaction>hi</compaction>");

        var json = SessionEventJson.Serialize(evt);
        var decoded = SessionEventJson.Deserialize(json);

        decoded.Should().BeOfType<ThreadCompacted>();
        ((ThreadCompacted)decoded).Text.Should().Be("<compaction>hi</compaction>");
    }

}

