namespace Agent.Acp.Acp;

/// <summary>
/// Abstraction for agent -> client JSON-RPC method calls.
/// Transport-bound.
/// </summary>
public interface IAcpClientCaller
{
    Task<TResponse> RequestAsync<TRequest, TResponse>(string method, TRequest request, CancellationToken cancellationToken = default);
}
