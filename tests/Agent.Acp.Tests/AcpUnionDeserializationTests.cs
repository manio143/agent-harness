using System.Text.Json;
using Agent.Acp.Protocol;
using Agent.Acp.Schema;

namespace Agent.Acp.Tests;

public class AcpUnionDeserializationTests
{
    [Fact]
    public void ContentBlock_Dispatches_By_Type_Discriminator()
    {
        const string json = """
        {
          "type": "text",
          "text": "hello"
        }
        """;

        var block = JsonSerializer.Deserialize<ContentBlock>(json, AcpJson.Options);

        Assert.NotNull(block);
        var t = Assert.IsType<TextContent>(block);
        Assert.Equal("hello", t.Text);
    }

    [Fact]
    public void ContentBlock_Unknown_Type_Falls_Back_To_UnknownContentBlock()
    {
        const string json = """
        {
          "type": "new_future_block",
          "foo": 123
        }
        """;

        var block = JsonSerializer.Deserialize<ContentBlock>(json, AcpJson.Options);

        var u = Assert.IsType<UnknownContentBlock>(block);
        Assert.Equal("new_future_block", u.Raw.GetProperty("type").GetString());
        Assert.Equal(123, u.Raw.GetProperty("foo").GetInt32());
    }

    [Fact]
    public void SessionUpdate_Dispatches_By_SessionUpdate_Discriminator()
    {
        const string json = """
        {
          "sessionUpdate": "agent_thought_chunk",
          "content": { "type": "text", "text": "thinking" }
        }
        """;

        var update = JsonSerializer.Deserialize<SessionUpdate>(json, AcpJson.Options);

        Assert.NotNull(update);
        var chunk = Assert.IsType<AgentThoughtChunk>(update);
        Assert.IsType<TextContent>(chunk.Content);
    }
}
