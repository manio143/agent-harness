using System.Text.Json;
using Agent.Harness.Llm;
using Xunit;

namespace Agent.Harness.Tests;

public sealed class ToolResultSanitizerTests
{
    [Fact]
    public void Sanitize_WhenStringIsHuge_TruncatesNestedStrings()
    {
        var el = JsonSerializer.SerializeToElement(new
        {
            content = new string('x', 2000),
            ok = true,
        });

        var r = ToolResultSanitizer.Sanitize(el, maxStringChars: 50);
        var json = JsonSerializer.Serialize(r.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.True(r.WasTruncated);
        Assert.Contains("\"content\"", json);
        Assert.Contains("TRUNCATED original_length=2000", json);
        Assert.Contains("\"ok\":true", json);
    }
}
