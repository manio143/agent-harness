using Agent.Harness.Threads;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class ChildThreadOrchestrationTests
{
    // Obsoleted by unification work: thread lifecycle is owned by ThreadOrchestrator.
    // Covered by:
    // - AcpEffectExecutorThreadStartUsesOrchestratorForkTests
    // - ThreadOrchestratorRequestForkChildThreadIntegrationTests
    //
    // Keeping file (empty) so git history shows the change; can be removed later.
}
