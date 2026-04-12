namespace Agent.Acp.Acp;

/// <summary>
/// Exception used internally to bubble a specific JSON-RPC error to the transport layer.
/// </summary>
public sealed class AcpJsonRpcException : Exception
{
    public AcpJsonRpcException(int code, string message) : base(message)
    {
        Code = code;
    }

    public int Code { get; }
}
