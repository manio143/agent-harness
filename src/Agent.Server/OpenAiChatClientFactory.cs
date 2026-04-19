using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using OpenAI;
using System.ClientModel;

namespace Agent.Server;

public sealed class OpenAiChatClientFactory : IChatClientFactory
{
    private readonly ModelCatalog _catalog;

    private readonly ConcurrentDictionary<string, IChatClient> _cache = new(StringComparer.OrdinalIgnoreCase);

    public OpenAiChatClientFactory(ModelCatalog catalog)
    {
        _catalog = catalog;
    }

    public IChatClient Get(string friendlyNameOrDefault)
    {
        // Normalize to a concrete friendly name (fall back to DefaultModel).
        var requested = string.IsNullOrWhiteSpace(friendlyNameOrDefault) || friendlyNameOrDefault == "default"
            ? _catalog.DefaultModel
            : friendlyNameOrDefault;

        var resolved = _catalog.Models.ContainsKey(requested) ? requested : _catalog.DefaultModel;

        // Cache per resolved name.
        return _cache.GetOrAdd(resolved, _ => Create(resolved));
    }

    private IChatClient Create(string friendlyName)
    {
        var opts = _catalog.Resolve(friendlyName);

        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = new Uri(opts.BaseUrl, UriKind.Absolute),
        };

        if (opts.NetworkTimeoutSeconds is { } seconds)
            clientOptions.NetworkTimeout = TimeSpan.FromSeconds(seconds);

        var openai = new OpenAIClient(new ApiKeyCredential(opts.ApiKey), clientOptions);
        var chat = openai.GetChatClient(opts.Model);

        return OpenAIClientExtensions.AsIChatClient(chat);
    }
}
