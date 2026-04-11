using System.Text.Json.Serialization;

namespace Agent.Acp.Schema;

// Adds explicit discriminator fields to content DTOs so callers don't have to push
// `type` via AdditionalProperties. These types are partial in AcpSchema.g.cs.

public partial class TextContent
{
    [JsonPropertyName("type")]
    public string Type => "text";
}

public partial class ImageContent
{
    [JsonPropertyName("type")]
    public string Type => "image";
}

public partial class AudioContent
{
    [JsonPropertyName("type")]
    public string Type => "audio";
}

public partial class ResourceLink
{
    [JsonPropertyName("type")]
    public string Type => "resource_link";
}

public partial class EmbeddedResource
{
    [JsonPropertyName("type")]
    public string Type => "resource";
}
