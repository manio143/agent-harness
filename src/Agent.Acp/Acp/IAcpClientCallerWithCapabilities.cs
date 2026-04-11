using Agent.Acp.Schema;

namespace Agent.Acp.Acp;

/// <summary>
/// Optional interface implemented by the server-provided client caller.
/// Exposes negotiated client capabilities from the initialize request.
/// </summary>
public interface IAcpClientCallerWithCapabilities : IAcpClientCaller
{
    ClientCapabilities ClientCapabilities { get; }
}
