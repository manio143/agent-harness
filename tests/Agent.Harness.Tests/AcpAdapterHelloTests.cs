using Agent.Harness.Acp;
using FluentAssertions;

namespace Agent.Harness.Tests;

public sealed class AcpAdapterHelloTests
{
    [Fact]
    public async Task PromptingThroughAcp_IsNotImplementedYet()
    {
        var adapter = new AcpSessionAgentAdapter();

        var act = async () =>
        {
            // We only care that this currently fails in a controlled way (TDD placeholder).
            await adapter.PromptAsync(null!, null!, CancellationToken.None);
        };

        await act.Should().ThrowAsync<NotImplementedException>();
    }
}
