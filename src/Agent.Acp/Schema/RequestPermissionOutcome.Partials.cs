using System.Text.Json.Serialization;

namespace Agent.Acp.Schema;

public partial class RequestPermissionOutcome
{
    public const string Cancelled = "cancelled";
    public const string Selected = "selected";

    [JsonPropertyName("outcome")]
    public string Outcome { get; set; } = default!;

    [JsonPropertyName("optionId")]
    public string? OptionId { get; set; }
}
