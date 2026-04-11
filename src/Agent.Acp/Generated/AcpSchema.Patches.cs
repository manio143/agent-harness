// NOTE: Temporary patch types for NJsonSchema generation gaps.
//
// The upstream ACP schema contains a number of anyOf/oneOf unions. NJsonSchema 11.0.2
// sometimes emits placeholder type names (e.g. Content1, ConfigOptions) without generating
// the corresponding definitions.
//
// We keep these as very permissive "extension objects" so the library compiles and we can
// build the protocol harness. As the generator config improves, this file should shrink.
//
// TODO: revisit generator settings and remove these fallbacks.

using System.Text.Json.Serialization;

namespace Agent.Acp.Schema;

public sealed class Content1
{
    [JsonExtensionData]
    public Dictionary<string, object> AdditionalProperties { get; set; } = new();
}

public sealed class ConfigOptions
{
    [JsonExtensionData]
    public Dictionary<string, object> AdditionalProperties { get; set; } = new();
}

public sealed class Update
{
    [JsonExtensionData]
    public Dictionary<string, object> AdditionalProperties { get; set; } = new();
}

public sealed class Outcome
{
    [JsonExtensionData]
    public Dictionary<string, object> AdditionalProperties { get; set; } = new();
}

public sealed class content
{
    [JsonExtensionData]
    public Dictionary<string, object> AdditionalProperties { get; set; } = new();
}

public enum Kind { }
public enum Kind2 { }
public enum Status { }
public enum Status2 { }
public enum Priority { }

public enum StopReason2
{
    EndTurn,
    Cancelled,
    Error,
}
