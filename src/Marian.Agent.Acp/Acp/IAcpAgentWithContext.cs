using Marian.Agent.Acp.Schema;

namespace Marian.Agent.Acp.Acp;

public interface IAcpAgentWithContext : IAcpAgent
{
    Task<PromptResponse> PromptAsync(PromptRequest request, IAcpAgentContext context, CancellationToken cancellationToken);
}

public interface IAcpAgentContext
{
    string SessionId { get; }

    Task SendSessionUpdateAsync(object update, CancellationToken cancellationToken = default);

    Task<TResponse> RequestAsync<TRequest, TResponse>(string method, TRequest request, CancellationToken cancellationToken = default);
}
