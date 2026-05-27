using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

/**
 When it comes to calling LLMs in Lilliput we are looking at a simple but powerful abstraction.
 The assumption is that rather than keeping a long running conversation with the model, we only provide it the necessary context to make a decision or produce some content.
 Because we're targeting small language models that are not always very capable and because we leverage constrained decoding to force the model to abide by the JSON schema we need it to produce
 the model call is split into 2 phases: a reasoning phase and an answer phase.
 - In the reasoning phase we ask the model to think through the problem and produce its reasoning in natural language.
 - In the answer phase we provide the model with its own reasoning and ask it to produce the final answer abiding by the provided JSON schema.
*/

/// <summary>
/// A contextual information to be provided to the model as part of the system prompt.
/// Each context part should be wrapped in some XML tags.
/// </summary>
public abstract class ContextPart {}

/// <summary>
/// A document represents a named piece of content the model can refer to.
/// </summary>
public class Document : ContextPart
{
    public required string Name { get; init; }
    public required virtual string Content { get; init; }

    public override string ToString()
    {
        return $"""<document name="{Name}">{Content}</document>""";
    }
}
public record class ConversationContext(List<ContextPart> Parts);

/// <summary>
/// An example of user-assistant interaction that can be provided to the model as part of the prompt to help it understand how to use the context and how to reason about it.
/// </summary>
/// <param name="UserMessage">Request</param>
/// <param name="AssistantReasoning">Reasoning</param>
/// <param name="AssistantMessage">Answer (preferably matching the JSON schema)</param>
public record class ConversationExample(string UserMessage, string? AssistantReasoning, string AssistantMessage)
{
    [JsonIgnore]
    public IEnumerable<ChatMessage> Messages
    {
        get
        {
            yield return new ChatMessage(ChatRole.User, UserMessage);
            if (!string.IsNullOrWhiteSpace(AssistantReasoning))
            {
                yield return new ChatMessage(ChatRole.Assistant, $"<reasoning>{AssistantReasoning}</reasoning>");
            }
            yield return new ChatMessage(ChatRole.Assistant, AssistantMessage);
        }
    }
}

/// <param name="RequestId">A unique ID for this request that can be used for correlation and logging.</param>
/// <param name="Instructions">System prompt - agent purpose, behavior guardrails, etc.</param>
/// <param name="ReasoningHint">Optional hint how to drive the reasoning phase. Suggested to include open questions.</param>
/// <param name="Request">The actual request/question for the model.</param>
public record class ModelRequest(
    Guid RequestId,
    string Instructions,
    ConversationContext Context,
    List<ConversationExample> Examples,
    string? ReasoningHint,
    string Request,
    JsonElement ResponseSchema
);

/// <summary>
/// Metadata about the call to the language model.
/// </summary>
public record ModelCallDetails(
    UsageDetails? ReasoningUsage,
    UsageDetails? ResponseUsage,
    string? ModelId,
    IEnumerable<ChatMessage> Messages
);

/// <param name="Response">JSON response for the request abiding by the schema</param>
public record class ModelResponse(
    Guid RequestId,
    JsonElement Response,
    ModelCallDetails Details
);

public static class ModelRequestProcessor
{
    public static async Task<ModelResponse> ProcessAsync(IChatClient chatClient, ModelRequest request)
    {
        ChatResponse response = await chatClient.GetResponseAsync(
            BuildMessages(request),
            new ChatOptions
            {
                Instructions = request.Instructions,
                ResponseFormat = ChatResponseFormat.ForJsonSchema(request.ResponseSchema),
            }
        );
        return new ModelResponse(
            RequestId: request.RequestId,
            Response: JsonDocument.Parse(response.Text).RootElement,
            Details: new ModelCallDetails(
                ReasoningUsage: null,
                ResponseUsage: response.Usage,
                ModelId: response.ModelId,
                Messages: BuildMessages(request).Concat(response.Messages)
            )
        );
    }

    public static async Task<ModelResponse> ProcessWithReasoningPassAsync(IChatClient chatClient, ModelRequest request)
    {
        ChatResponse reasoningResponse = await chatClient.GetResponseAsync(
            BuildMessagesForReasoningPass(request),
            new ChatOptions
            {
                Instructions = request.Instructions,
                ResponseFormat = ChatResponseFormat.Text,
            }
        );
        string reasoning = reasoningResponse.Text;
        ChatResponse response = await chatClient.GetResponseAsync(
            BuildMessagesForAnswerPass(request, reasoning),
            new ChatOptions
            {
                Instructions = request.Instructions,
                ResponseFormat = ChatResponseFormat.ForJsonSchema(request.ResponseSchema),
                ConversationId = reasoningResponse.ConversationId, // keep the same conversation in case it affects caching
            }
        );
        return new ModelResponse(
            RequestId: Guid.NewGuid(),
            Response: JsonDocument.Parse(response.Text).RootElement,
            Details: new ModelCallDetails(
                ReasoningUsage: reasoningResponse.Usage,
                ResponseUsage: response.Usage,
                ModelId: response.ModelId,
                Messages: BuildMessagesForAnswerPass(request, reasoning).Concat(response.Messages)
            )
        );
    }

    private static IEnumerable<ChatMessage> BuildMessages(ModelRequest request)
    {
        foreach (var message in request.GetContextMessages())
            yield return message;

        foreach (var message in request.GetExampleMessages())
            yield return message;

        yield return new ChatMessage(ChatRole.User, request.Request);
    }

    private static IEnumerable<ChatMessage> BuildMessagesForReasoningPass(ModelRequest request)
    {
        foreach (var message in request.GetContextMessages())
            yield return message;

        foreach(var message in request.GetExampleMessages())
            yield return message;

        yield return new ChatMessage(ChatRole.User, $"""
        Your task is to deeply think through how to answer the user's request given the instructions, context and any earlier examples of communication.
        I'm expecting a paragraph or more of reasoning that shows your thorough thought process.
        {(!string.IsNullOrWhiteSpace(request.ReasoningHint) ? $"<reasoning_hint>{request.ReasoningHint}</reasoning_hint>" : string.Empty)}
        <user_request>
        {request.Request}
        </user_request>
        Your reasoning:
        """);
    }
    private static IEnumerable<ChatMessage> BuildMessagesForAnswerPass(ModelRequest request, string reasoning)
    {
        foreach (var message in BuildMessagesForReasoningPass(request))
            yield return message;

        yield return new ChatMessage(ChatRole.Assistant, reasoning);

        yield return new ChatMessage(ChatRole.User, $"""
        Now provide your final answer to the user's original question:
        <user_request>
        {request.Request}
        </user_request>
        """);
    }
}

public static class ModelRequestExtensions
{
    public static IEnumerable<ChatMessage> GetContextMessages(this ModelRequest request)
    {
        foreach (var doc in request.Context.Parts)
        {
            yield return new ChatMessage(ChatRole.System, [new TextContent(doc.ToString())]);
        }
    }

    public static IEnumerable<ChatMessage> GetExampleMessages(this ModelRequest request)
    {
        foreach (var example in request.Examples)
        {
            foreach (var message in example.Messages)
            {
                yield return message;
            }
        }
    }
}