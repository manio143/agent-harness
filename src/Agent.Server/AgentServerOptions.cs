namespace Agent.Server;

public sealed class AgentServerOptions
{
    public OpenAiOptions OpenAI { get; set; } = new();
    public SessionStoreOptions Sessions { get; set; } = new();
    public LoggingOptions Logging { get; set; } = new();
    public CoreOptions Core { get; set; } = new();
    public AcpOptions Acp { get; set; } = new();

    public sealed class OpenAiOptions
    {
        public string BaseUrl { get; set; } = "http://ollama-api:11434/v1";
        public string Model { get; set; } = "qwen2.5:3b";
        public string ApiKey { get; set; } = "ollama";

        /// <summary>
        /// Network timeout for the underlying OpenAI-compatible HTTP client.
        /// Default is left null to use library defaults.
        /// </summary>
        public int? NetworkTimeoutSeconds { get; set; }
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

    public sealed class AcpOptions
    {
        public bool PublishReasoning { get; set; } = false;
    }
}
