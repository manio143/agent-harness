using Agent.Acp.TypeGen;
using NJsonSchema;

namespace Agent.Acp.Tests;

public class TypeGenPostProcessorTests
{
    [Fact]
    public void Rewrites_PlaceholderType_To_ContentBlock_BasedOnSchemaRef_NotPropertyName()
    {
        // Arrange a minimal schema:
        // - defines ContentBlock
        // - defines Holder with property "payload" referencing ContentBlock
        var schema = new JsonSchema();
        var contentBlock = new JsonSchema { Title = "ContentBlock" };
        schema.Definitions["ContentBlock"] = contentBlock;

        var holder = new JsonSchema { Title = "Holder" };
        holder.Properties["payload"] = new JsonSchemaProperty { Reference = contentBlock };
        schema.Definitions["Holder"] = holder;

        var input = """
        namespace Agent.Acp.Schema
        {
            public partial class Holder
            {
                [System.Text.Json.Serialization.JsonPropertyName("payload")]
                public Content1 Payload { get; set; } = default!;
            }
        }
        """;

        var output = CodegenPostProcessor.PostProcessGeneratedCode(schema, input);

        Assert.Contains("public ContentBlock Payload", output);
        Assert.DoesNotContain("public Content1 Payload", output);
    }

    [Fact]
    public void DoesNothing_When_ContentBlock_Definition_Missing()
    {
        var schema = new JsonSchema();

        var input = """
        namespace Agent.Acp.Schema
        {
            public partial class ContentChunk
            {
                [System.Text.Json.Serialization.JsonPropertyName("content")]
                public Content1 Content { get; set; } = default!;
            }
        }
        """;

        var output = CodegenPostProcessor.PostProcessGeneratedCode(schema, input);

        Assert.Equal(input, output);
    }

    [Fact]
    public void Is_Idempotent()
    {
        var schema = new JsonSchema();
        schema.Definitions["ContentBlock"] = new JsonSchema();

        var input = """
        namespace Agent.Acp.Schema
        {
            public partial class ContentChunk
            {
                [System.Text.Json.Serialization.JsonPropertyName("content")]
                public Content1 Content { get; set; } = default!;
            }
        }
        """;

        var once = CodegenPostProcessor.PostProcessGeneratedCode(schema, input);
        var twice = CodegenPostProcessor.PostProcessGeneratedCode(schema, once);

        Assert.Equal(once, twice);
    }
}
