using Agent.Acp.Acp;
using Agent.Acp.Transport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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

        // Load dotnet user-secrets (opt-in by environment) so local runs can keep API keys out of appsettings.json.
        if (builder.Environment.IsDevelopment())
            builder.Configuration.AddUserSecrets(typeof(Program).Assembly, optional: true);

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

        // NOTE: session stores are created per-session and rooted at ACP client-provided CWD.
        // (see AcpHarnessAgentFactory)

        builder.Services.AddSingleton(sp => ModelCatalog.FromOptions(sp.GetRequiredService<AgentServerOptions>()));

        builder.Services.AddSingleton<IChatClientFactory, OpenAiChatClientFactory>();

        // Back-compat: default chat client is still registered for call sites that aren't model-aware yet.
        builder.Services.AddSingleton<Microsoft.Extensions.AI.IChatClient>(sp =>
        {
            var catalog = sp.GetRequiredService<ModelCatalog>();
            var factory = sp.GetRequiredService<IChatClientFactory>();
            return factory.Get(catalog.DefaultModel);
        });

        builder.Services.AddSingleton<IAcpAgentFactory>(sp =>
        {
            var chat = sp.GetRequiredService<Microsoft.Extensions.AI.IChatClient>();
            var factory = sp.GetRequiredService<IChatClientFactory>();
            var catalog = sp.GetRequiredService<ModelCatalog>();
            var opts = sp.GetRequiredService<AgentServerOptions>();
            return new AcpHarnessAgentFactory(chat, factory, catalog, opts);
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

        var serverOptions = host.Services.GetRequiredService<AgentServerOptions>();

        await using Agent.Acp.Transport.ITransport transport = serverOptions.Logging.LogRpc
            ? new RpcLoggingTransport(baseTransport)
            : baseTransport;

        if (serverOptions.Logging.LogRpc)
            logger.LogWarning("RPC logging enabled (stderr). May include user content.");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        await server.RunAsync(transport, cts.Token);
    }
}
