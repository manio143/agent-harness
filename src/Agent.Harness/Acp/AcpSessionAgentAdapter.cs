using Agent.Acp.Acp;
using Agent.Acp.Schema;

namespace Agent.Harness.Acp;

/// <summary>
/// ACP adapter. This will evolve to:
/// - translate ACP prompt content into observed events
/// - run MEAI streaming
/// - feed observed events into Core.Reduce
/// - publish ONLY committed events as ACP session/update
/// </summary>
public sealed class AcpSessionAgentAdapter : IAcpSessionAgent
{
    public async Task<PromptResponse> PromptAsync(PromptRequest request, IAcpPromptTurn turn, CancellationToken cancellationToken)
    {
        // TDD: implement after reducer + streaming policy tests exist.
        await Task.Yield();
        throw new NotImplementedException();
    }
}
