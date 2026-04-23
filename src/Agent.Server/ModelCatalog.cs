using System.Collections.ObjectModel;

namespace Agent.Server;

public sealed record ModelCatalog(
    IReadOnlyDictionary<string, AgentServerOptions.OpenAiModelOptions> Models,
    string DefaultModel,
    string QuickWorkModel)
{
    public static ModelCatalog FromOptions(AgentServerOptions opts)
    {
        if (opts is null) throw new ArgumentNullException(nameof(opts));

        var dict = new Dictionary<string, AgentServerOptions.OpenAiModelOptions>(StringComparer.OrdinalIgnoreCase);

        if (opts.Models.Catalog is { Count: > 0 })
        {
            foreach (var (k, v) in opts.Models.Catalog)
                dict[k] = v;
        }
        else
        {
            // Back-compat: old single OpenAI block.
            dict["default"] = opts.OpenAI;
        }

        var defaultModel = dict.ContainsKey(opts.Models.DefaultModel)
            ? opts.Models.DefaultModel
            : "default";

        if (!dict.ContainsKey(defaultModel))
            dict["default"] = opts.OpenAI;

        var quick = dict.ContainsKey(opts.Models.QuickWorkModel)
            ? opts.Models.QuickWorkModel
            : defaultModel;

        return new ModelCatalog(
            Models: new ReadOnlyDictionary<string, AgentServerOptions.OpenAiModelOptions>(dict),
            DefaultModel: defaultModel,
            QuickWorkModel: quick);
    }

    public bool IsKnownModel(string friendlyName)
    {
        if (string.IsNullOrWhiteSpace(friendlyName)) return false;
        if (string.Equals(friendlyName, "default", StringComparison.OrdinalIgnoreCase)) return true;
        return Models.ContainsKey(friendlyName);
    }

    public AgentServerOptions.OpenAiModelOptions Resolve(string friendlyNameOrDefault)
    {
        var name = string.IsNullOrWhiteSpace(friendlyNameOrDefault) || friendlyNameOrDefault == "default"
            ? DefaultModel
            : friendlyNameOrDefault;

        if (Models.TryGetValue(name, out var m))
            return m;

        return Models[DefaultModel];
    }

    public int? TryGetContextWindowTokensByProviderModel(string providerModel)
    {
        if (string.IsNullOrWhiteSpace(providerModel)) return null;

        foreach (var m in Models.Values)
        {
            if (!string.Equals(m.Model, providerModel, StringComparison.Ordinal))
                continue;

            if (m.ContextWindowK is null)
                return null;

            return m.ContextWindowK.Value * 1000;
        }

        return null;
    }
}
