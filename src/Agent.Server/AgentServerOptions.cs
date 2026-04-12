namespace Agent.Server;

public sealed class AgentServerOptions
{
    public OpenAiOptions OpenAI { get; set; } = new();
    public SessionStoreOptions Sessions { get; set; } = new();
    public CoreOptions Core { get; set; } = new();
    public AcpOptions Acp { get; set; } = new();

    public sealed class OpenAiOptions
    {
        public string BaseUrl { get; set; } = "http://ollama-api:11434/v1";
        public string Model { get; set; } = "qwen2.5:3b";
        public string ApiKey { get; set; } = "ollama";
    }

    public sealed class SessionStoreOptions
    {
        public string Directory { get; set; } = ".agent/sessions";
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
