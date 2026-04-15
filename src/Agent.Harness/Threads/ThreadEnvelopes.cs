namespace Agent.Harness.Threads;

public static class ThreadEnvelopes
{
    public static string NewEnvelopeId() => Guid.NewGuid().ToString("N");
}
