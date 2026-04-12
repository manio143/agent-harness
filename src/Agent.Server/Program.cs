using Agent.Acp.Acp;
using Agent.Acp.Transport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAI;
using System.ClientModel;
using Microsoft.Extensions.AI;

namespace Agent.Server;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.Configuration
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .AddEnvironmentVariables(prefix: "AGENTSERVER_");

        builder.Services.AddLogging(o =>
        {
            o.ClearProviders();
            o.AddConsole(c =>
            {
                c.LogToStandardErrorThreshold = LogLevel.Information;
            });
        });

        builder.Services.AddSingleton(sp =>
        {
            var opts = new AgentServerOptions();
            sp.GetRequiredService<IConfiguration>().GetSection("AgentServer").Bind(opts);
            return opts;
        });

        builder.Services.AddSingleton<Agent.Harness.Persistence.ISessionStore>(sp =>
        {
            var opts = sp.GetRequiredService<AgentServerOptions>();
            var root = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), opts.Sessions.Directory));
            return new Agent.Harness.Persistence.JsonlSessionStore(root);
        });

        // MEAI OpenAI adapter (Ollama is OpenAI-compatible when pointed at /v1).
        builder.Services.AddSingleton<Microsoft.Extensions.AI.IChatClient>(sp =>
        {
            var opts = sp.GetRequiredService<AgentServerOptions>();

            var clientOptions = new OpenAIClientOptions
            {
                Endpoint = new Uri(opts.OpenAI.BaseUrl, UriKind.Absolute),
            };

            var openai = new OpenAIClient(new ApiKeyCredential(opts.OpenAI.ApiKey), clientOptions);
            var chat = openai.GetChatClient(opts.OpenAI.Model);

            return Microsoft.Extensions.AI.OpenAIClientExtensions.AsIChatClient(chat);
        });

        builder.Services.AddSingleton<IAcpAgentFactory>(sp =>
        {
            var chat = sp.GetRequiredService<Microsoft.Extensions.AI.IChatClient>();
            var opts = sp.GetRequiredService<AgentServerOptions>();
            var store = sp.GetRequiredService<Agent.Harness.Persistence.ISessionStore>();
            return new AcpHarnessAgentFactory(chat, opts, store);
        });

        using var host = builder.Build();

        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Agent.Server");
        logger.LogInformation("Starting ACP stdio server...");

        var factory = host.Services.GetRequiredService<IAcpAgentFactory>();
        var server = new AcpAgentServer(factory);

        await using var baseTransport = new LineDelimitedStreamTransport(
            input: Console.OpenStandardInput(),
            output: Console.OpenStandardOutput(),
            name: "stdio");

        await using Agent.Acp.Transport.ITransport transport = host.Services.GetRequiredService<AgentServerOptions>().Logging.LogRpc
            ? new RpcLoggingTransport(baseTransport)
            : baseTransport;

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        await server.RunAsync(transport, cts.Token);
    }
}
