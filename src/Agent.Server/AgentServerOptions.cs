namespace Agent.Server;

public sealed class AgentServerOptions
{
    // Back-compat: original single OpenAI-compatible model block.
    // New code should use Models + friendly names.
    public OpenAiOptions OpenAI { get; set; } = new();

    public ModelsOptions Models { get; set; } = new();

    public SessionStoreOptions Sessions { get; set; } = new();
    public LoggingOptions Logging { get; set; } = new();
    public CoreOptions Core { get; set; } = new();
    public CompactionOptions Compaction { get; set; } = new();
    public ThreadingOptions Threading { get; set; } = new();
    public AcpOptions Acp { get; set; } = new();

    public sealed class ModelsOptions
    {
        /// <summary>
        /// Friendly-name model catalog.
        /// Example: "granite" -> { baseUrl, apiKey, model }
        /// </summary>
        public Dictionary<string, OpenAiModelOptions> Catalog { get; set; } = new();

        /// <summary>
        /// Friendly name used when no thread-specific model is configured.
        /// Defaults to "default".
        /// </summary>
        public string DefaultModel { get; set; } = "default";

        /// <summary>
        /// Friendly name used for quick work outside main thread turns (e.g. title generation).
        /// Defaults to DefaultModel.
        /// </summary>
        public string QuickWorkModel { get; set; } = "default";
    }

    public class OpenAiModelOptions
    {
        public string BaseUrl { get; set; } = "http://ollama-api:11434/v1";
        public string Model { get; set; } = "qwen2.5:3b";
        public string ApiKey { get; set; } = "ollama";

        /// <summary>
        /// Context window size in thousands of tokens (e.g. 4 => ~4000 tokens).
        /// MEAI does not expose this for automated discovery.
        /// If null, context window is treated as unknown.
        /// </summary>
        public int? ContextWindowK { get; set; }

        /// <summary>
        /// Network timeout for the underlying OpenAI-compatible HTTP client.
        /// Default is left null to use library defaults.
        /// </summary>
        public int? NetworkTimeoutSeconds { get; set; }

        /// <summary>
        /// Optional max output token cap (completion limit) for this model.
        /// Useful for providers with low TPM limits.
        /// If null, provider defaults apply.
        /// </summary>
        public int? MaxOutputTokens { get; set; }
    }

    public sealed class OpenAiOptions : OpenAiModelOptions
    {
    }

    public sealed class SessionStoreOptions
    {
        public string Directory { get; set; } = ".agent/sessions";
    }

    public sealed class LoggingOptions
    {
        public bool LogRpc { get; set; } = false;

        /// <summary>
        /// Opt-in logging of the exact messages + tool declarations sent to the LLM.
        /// Warning: may include user content and file/tool outputs.
        /// </summary>
        public bool LogLlmPrompts { get; set; } = false;

        /// <summary>
        /// Opt-in logging of every observed event (including tool intent + tool execution observations)
        /// into {sessionsDir}/{sessionId}/observed.jsonl.
        /// </summary>
        public bool LogObservedEvents { get; set; } = false;
    }

    public sealed class CoreOptions
    {
        public bool CommitAssistantTextDeltas { get; set; } = true;
        public bool CommitReasoningTextDeltas { get; set; } = false;
    }

    public sealed class CompactionOptions
    {
        /// <summary>
        /// When totalTokens / contextWindowTokens >= threshold, schedule compaction.
        /// Default: 0.90.
        /// </summary>
        public double Threshold { get; set; } = 0.90;

        /// <summary>
        /// Number of user/assistant messages to retain verbatim after compaction.
        /// Default: 5.
        /// </summary>
        public int TailMessageCount { get; set; } = 5;

        /// <summary>
        /// Optional safety valve: truncate individual tail user/assistant messages to this many characters
        /// when building post-compaction prompts. Null disables truncation.
        /// </summary>
        public int? MaxTailMessageChars { get; set; } = null;

        /// <summary>
        /// Friendly model name used for compaction runs.
        /// Default: "default".
        /// </summary>
        public string Model { get; set; } = "default";
    }

    public sealed class ThreadingOptions
    {
        /// <summary>
        /// Optional capabilities applied to the main thread at session start.
        /// This is the preferred way to restrict tools for constrained providers.
        /// </summary>
        public CapabilitiesOptions MainThreadCapabilities { get; set; } = new();

        public sealed class CapabilitiesOptions
        {
            public string[] Allow { get; set; } = Array.Empty<string>();
            public string[] Deny { get; set; } = Array.Empty<string>();
        }
    }

    public sealed class AcpOptions
    {
        public bool PublishReasoning { get; set; } = false;
    }
}
