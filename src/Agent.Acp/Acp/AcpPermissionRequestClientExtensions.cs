using Agent.Acp.Schema;

namespace Agent.Acp.Acp;

public static class AcpPermissionRequestClientExtensions
{
    public static Task<RequestPermissionResponse> RequestPermissionAsync(this IAcpClientCaller client, RequestPermissionRequest request, CancellationToken cancellationToken = default)
        => client.RequestAsync<RequestPermissionRequest, RequestPermissionResponse>("session/request_permission", request, cancellationToken);
}
