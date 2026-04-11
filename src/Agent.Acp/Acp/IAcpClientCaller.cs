using Agent.Acp.Schema;

namespace Agent.Acp.Acp;

/// <summary>
/// Abstraction for agent -> client JSON-RPC method calls.
/// Transport-bound.
/// </summary>
public interface IAcpClientCaller
{
    /// <summary>
    /// Negotiated client capabilities from the initialize request.
    /// Agents MUST check these before calling optional client methods.
    /// </summary>
    ClientCapabilities ClientCapabilities { get; }

    Task<TResponse> RequestAsync<TRequest, TResponse>(string method, TRequest request, CancellationToken cancellationToken = default);
}
