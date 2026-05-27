using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenAI;
using OpenAI.Chat;

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration.AddEnvironmentVariables();
builder.Configuration.AddUserSecrets(typeof(Program).Assembly, optional: true);

// Example usage
// OpenAI:Model = "model-name"
// OpenAI:ApiKey = "secret"
// OpenAI:Options:Endpoint = "http://localhost:1234/v1"
// OpenAI:Options:NetworkTimeout = "00:03:30"
builder.Services.AddSingleton<ChatClientSettings>(sp =>
{
    var settings = new ChatClientSettings();
    settings.Bind(builder.Configuration.GetSection("OpenAI"));
    return settings;
});
builder.Services.AddSingleton<IChatClient>(sp =>
{
    var settings = sp.GetRequiredService<ChatClientSettings>();
    var opts = settings.Options ?? new OpenAIClientOptions();
    var client = new OpenAIClient(new ApiKeyCredential(builder.Configuration["OpenAI:ApiKey"] ?? ""), opts);
    return client.GetChatClient(settings.Model).AsIChatClient();
});

var app = builder.Build();
var chat = app.Services.GetRequiredService<IChatClient>();

var request = new ModelRequest(
    RequestId: Guid.NewGuid(),
    Instructions: "You are a helpful assistant that answers questions about the provided document.",
    Context: new ConversationContext(new List<ContextPart>
    {
        new Document { Name = "test.doc", Content = "The quick brown fox jumps over the lazy dog." },
    }),
    Examples: new List<ConversationExample>
    {
        new ConversationExample(
            UserMessage: "What does the fox jump over?",
            AssistantReasoning: "Given the document I must select the words from the document. The user asked what the fox jump over and in the document the fox jumps over a dog. Moreover, the dog is lazy. So the answer is: the lazy dog.",
            AssistantMessage: "{ \"message\": \"the lazy dog\" }"
        ),
    },
    ReasoningHint: null,
    Request: "What color is the fox?",
    ResponseSchema: JsonDocument.Parse(@"{
        ""type"": ""object"",
        ""properties"": {
            ""message"": { ""type"": ""string"" }
        },
        ""required"": [""message""]
    }").RootElement
);
Console.WriteLine(JsonSerializer.Serialize(request, new JsonSerializerOptions { WriteIndented = true }));

var response = await ModelRequestProcessor.ProcessWithReasoningPassAsync(chat, request);

Console.WriteLine(JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true }));