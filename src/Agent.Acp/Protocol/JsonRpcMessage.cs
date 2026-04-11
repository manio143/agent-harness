using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agent.Acp.Protocol;

/// <summary>
/// Minimal JSON-RPC 2.0 envelope model.
///
/// This layer is protocol-agnostic and intentionally small; ACP-specific request/response
/// DTOs are generated from ACP's JSON Schema under <c>Agent.Acp.Schema</c>.
/// </summary>
[JsonConverter(typeof(JsonRpcMessageConverter))]
public abstract class JsonRpcMessage
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    /// <summary>
    /// Optional per-message context. Not part of JSON-RPC; for callers to attach extra metadata.
    /// </summary>
    [JsonIgnore]
    public object? Context { get; set; }
}

public abstract class JsonRpcMessageWithId : JsonRpcMessage
{
    [JsonPropertyName("id")]
    public required JsonElement Id { get; set; }
}

public sealed class JsonRpcRequest : JsonRpcMessageWithId
{
    [JsonPropertyName("method")]
    public required string Method { get; set; }

    [JsonPropertyName("params")]
    public JsonElement? Params { get; set; }
}

public sealed class JsonRpcNotification : JsonRpcMessage
{
    [JsonPropertyName("method")]
    public required string Method { get; set; }

    [JsonPropertyName("params")]
    public JsonElement? Params { get; set; }
}

public sealed class JsonRpcResponse : JsonRpcMessageWithId
{
    [JsonPropertyName("result")]
    public JsonElement Result { get; set; }
}

public sealed class JsonRpcError : JsonRpcMessageWithId
{
    [JsonPropertyName("error")]
    public required JsonRpcErrorDetail Error { get; set; }
}

public sealed class JsonRpcErrorDetail
{
    [JsonPropertyName("code")]
    public required int Code { get; set; }

    [JsonPropertyName("message")]
    public required string Message { get; set; }

    [JsonPropertyName("data")]
    public JsonElement? Data { get; set; }
}

internal sealed class JsonRpcMessageConverter : JsonConverter<JsonRpcMessage>
{
    public override JsonRpcMessage? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        // Detect JSON-RPC type by fields.
        bool hasMethod = root.TryGetProperty("method", out _);
        bool hasId = root.TryGetProperty("id", out _);
        bool hasResult = root.TryGetProperty("result", out _);
        bool hasError = root.TryGetProperty("error", out _);

        if (hasMethod && hasId)
        {
            return root.Deserialize<JsonRpcRequest>(options);
        }

        if (hasMethod && !hasId)
        {
            return root.Deserialize<JsonRpcNotification>(options);
        }

        if (hasId && hasResult)
        {
            return root.Deserialize<JsonRpcResponse>(options);
        }

        if (hasId && hasError)
        {
            return root.Deserialize<JsonRpcError>(options);
        }

        throw new JsonException("Unrecognized JSON-RPC message shape.");
    }

    public override void Write(Utf8JsonWriter writer, JsonRpcMessage value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case JsonRpcRequest req:
                JsonSerializer.Serialize(writer, req, options);
                return;
            case JsonRpcNotification notif:
                JsonSerializer.Serialize(writer, notif, options);
                return;
            case JsonRpcResponse resp:
                JsonSerializer.Serialize(writer, resp, options);
                return;
            case JsonRpcError err:
                JsonSerializer.Serialize(writer, err, options);
                return;
            default:
                throw new JsonException($"Unsupported JsonRpcMessage type: {value.GetType().FullName}");
        }
    }
}
