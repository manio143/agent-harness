using Agent.Acp.TypeGen;
using NJsonSchema;

namespace Agent.Acp.Tests;

public class TypeGenPostProcessorTests
{
    [Fact]
    public void Rewrites_Content_PlaceholderType_To_ContentBlock()
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

        var output = CodegenPostProcessor.PostProcessGeneratedCode(schema, input);

        Assert.Contains("public ContentBlock Content", output);
        Assert.DoesNotContain("public Content1 Content", output);
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
