using System;
using System.Threading;
using System.Threading.Tasks;
using Agent.Acp.Acp;
using Agent.Acp.Schema;

namespace Agent.Acp.Client.AvaloniaApp.Services.Acp;

public static class AcpClientBootstrap
{
    public static Task<InitializeResponse> InitializeAsync(AcpClientConnection conn, CancellationToken cancellationToken = default)
    {
        // Minimal client capabilities for now. We can expand once we implement fs/terminal handlers.
        var req = new InitializeRequest
        {
            ProtocolVersion = 1,
            ClientInfo = new ClientInfo
            {
                AdditionalProperties =
                {
                    ["name"] = "Agent.Acp.Client.AvaloniaApp",
                    ["version"] = "0.1",
                }
            },
            ClientCapabilities = new ClientCapabilities(),
        };

        return conn.RequestAsync<InitializeRequest, InitializeResponse>("initialize", req, cancellationToken);
    }

    public static Task<NewSessionResponse> NewSessionAsync(AcpClientConnection conn, string cwd, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cwd) || !System.IO.Path.IsPathRooted(cwd))
            throw new ArgumentException("cwd must be an absolute path", nameof(cwd));

        var req = new NewSessionRequest
        {
            Cwd = cwd,
        };

        return conn.RequestAsync<NewSessionRequest, NewSessionResponse>("session/new", req, cancellationToken);
    }

    public static Task<ListSessionsResponse> ListSessionsAsync(AcpClientConnection conn, string? cwd = null, string? cursor = null, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(cwd) && !System.IO.Path.IsPathRooted(cwd))
            throw new ArgumentException("cwd must be an absolute path", nameof(cwd));

        var req = new ListSessionsRequest
        {
            Cwd = cwd,
            Cursor = cursor,
        };

        return conn.RequestAsync<ListSessionsRequest, ListSessionsResponse>("session/list", req, cancellationToken);
    }

    public static Task<LoadSessionResponse> LoadSessionAsync(AcpClientConnection conn, string sessionId, string cwd, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("sessionId is required", nameof(sessionId));

        if (string.IsNullOrWhiteSpace(cwd) || !System.IO.Path.IsPathRooted(cwd))
            throw new ArgumentException("cwd must be an absolute path", nameof(cwd));

        var req = new LoadSessionRequest
        {
            SessionId = sessionId,
            Cwd = cwd,
        };

        return conn.RequestAsync<LoadSessionRequest, LoadSessionResponse>("session/load", req, cancellationToken);
    }

    public static Task<PromptResponse> PromptAsync(AcpClientConnection conn, string sessionId, string text, CancellationToken cancellationToken = default)
    {
        var req = new PromptRequest
        {
            SessionId = sessionId,
            Prompt = new[]
            {
                new TextContent { Text = text }
            },
        };

        return conn.RequestAsync<PromptRequest, PromptResponse>("session/prompt", req, cancellationToken);
    }
}
