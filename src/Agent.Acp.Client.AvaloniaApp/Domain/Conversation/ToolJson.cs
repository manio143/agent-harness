using System.Text.Json;

namespace Agent.Acp.Client.AvaloniaApp.Domain.Conversation;

internal static class ToolJson
{
    public static bool HasMeaningful(object value)
    {
        if (value is null) return false;

        if (value is JsonElement e)
        {
            return e.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null;
        }

        return true;
    }

    public static string? TryStringify(object value)
    {
        try
        {
            if (value is null) return null;
            if (value is JsonElement e)
            {
                if (e.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;
                return e.GetRawText();
            }

            return JsonSerializer.Serialize(value);
        }
        catch
        {
            return null;
        }
    }
}
