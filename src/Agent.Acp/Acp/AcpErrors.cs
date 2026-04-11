namespace Agent.Acp.Acp;

internal static class AcpErrors
{
    // JSON-RPC standard codes
    public const int InvalidParams = -32602;
    public const int MethodNotFound = -32601;

    // Server error range (-32000..-32099) reserved for implementation.
    public const int NotInitialized = -32000;
}
