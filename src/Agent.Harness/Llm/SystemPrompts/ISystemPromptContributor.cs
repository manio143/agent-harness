namespace Agent.Harness.Llm.SystemPrompts;

public interface ISystemPromptContributor
{
    IEnumerable<SystemPromptFragment> Build(SystemPromptContext ctx);
}
