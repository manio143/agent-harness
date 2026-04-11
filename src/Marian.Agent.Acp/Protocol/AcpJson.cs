using System.Text.Json;
using System.Text.Json.Serialization;

namespace Marian.Agent.Acp.Protocol;

public static class AcpJson
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
        };

        // Needed for JsonRpcMessage polymorphism
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));

        return o;
    }
}
