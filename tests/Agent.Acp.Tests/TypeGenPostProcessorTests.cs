using Agent.Acp.TypeGen;
using NJsonSchema;

namespace Agent.Acp.Tests;

public class TypeGenPostProcessorTests
{
    [Fact]
    public void Rewrites_ContentBlock_PlaceholderType_BasedOnSchemaRef_NotPropertyName()
    {
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
    public void Rewrites_SessionUpdate_PlaceholderType()
    {
        var schema = new JsonSchema();
        var sessionUpdate = new JsonSchema { DocumentPath = "#/definitions/SessionUpdate" };
        schema.Definitions["SessionUpdate"] = sessionUpdate;

        var holder = new JsonSchema { Title = "Holder" };
        holder.Properties["update"] = new JsonSchemaProperty { Reference = sessionUpdate };
        schema.Definitions["Holder"] = holder;

        var input = """
        namespace Agent.Acp.Schema
        {
            public partial class Holder
            {
                [System.Text.Json.Serialization.JsonPropertyName("update")]
                public Update Update { get; set; } = default!;
            }
        }
        """;

        var output = CodegenPostProcessor.PostProcessGeneratedCode(schema, input);

        Assert.Contains("public SessionUpdate Update", output);
        Assert.DoesNotContain("public Update Update", output);
    }

    [Fact]
    public void Rewrites_RequestPermissionOutcome_PlaceholderType()
    {
        var schema = new JsonSchema();
        var outcome = new JsonSchema { DocumentPath = "#/definitions/RequestPermissionOutcome" };
        schema.Definitions["RequestPermissionOutcome"] = outcome;

        var holder = new JsonSchema { Title = "Holder" };
        holder.Properties["outcome"] = new JsonSchemaProperty { Reference = outcome };
        schema.Definitions["Holder"] = holder;

        var input = """
        namespace Agent.Acp.Schema
        {
            public partial class Holder
            {
                [System.Text.Json.Serialization.JsonPropertyName("outcome")]
                public Outcome Outcome { get; set; } = default!;
            }
        }
        """;

        var output = CodegenPostProcessor.PostProcessGeneratedCode(schema, input);

        Assert.Contains("public RequestPermissionOutcome Outcome", output);
        Assert.DoesNotContain("public Outcome Outcome", output);
    }

    [Fact]
    public void Rewrites_ArrayItem_PlaceholderType_To_SessionConfigOption()
    {
        var schema = new JsonSchema();
        var opt = new JsonSchema { DocumentPath = "#/definitions/SessionConfigOption" };
        schema.Definitions["SessionConfigOption"] = opt;

        var holder = new JsonSchema { Title = "Holder" };
        holder.Properties["configOptions"] = new JsonSchemaProperty
        {
            Type = NJsonSchema.JsonObjectType.Array,
            Item = new JsonSchema { Reference = opt }
        };
        schema.Definitions["Holder"] = holder;

        var input = """
        namespace Agent.Acp.Schema
        {
            public partial class Holder
            {
                [System.Text.Json.Serialization.JsonPropertyName("configOptions")]
                public System.Collections.Generic.ICollection<ConfigOptions> ConfigOptions { get; set; } = default!;
            }
        }
        """;

        var output = CodegenPostProcessor.PostProcessGeneratedCode(schema, input);

        Assert.Contains("ICollection<SessionConfigOption> ConfigOptions", output);
        Assert.DoesNotContain("ICollection<ConfigOptions> ConfigOptions", output);
    }

    [Fact]
    public void Rewrites_ToolCallKind_And_Status_Placeholders()
    {
        var schema = new JsonSchema();

        var toolKind = new JsonSchema { DocumentPath = "#/definitions/ToolKind" };
        var toolCallStatus = new JsonSchema { DocumentPath = "#/definitions/ToolCallStatus" };
        schema.Definitions["ToolKind"] = toolKind;
        schema.Definitions["ToolCallStatus"] = toolCallStatus;

        var holder = new JsonSchema { Title = "Holder" };
        holder.Properties["kind"] = new JsonSchemaProperty { Reference = toolKind };
        holder.Properties["status"] = new JsonSchemaProperty { Reference = toolCallStatus };
        schema.Definitions["Holder"] = holder;

        var input = """
        namespace Agent.Acp.Schema
        {
            public partial class Holder
            {
                [System.Text.Json.Serialization.JsonPropertyName("kind")]
                public Kind2 Kind { get; set; } = default!;

                [System.Text.Json.Serialization.JsonPropertyName("status")]
                public Status3 Status { get; set; } = default!;
            }
        }
        """;

        var output = CodegenPostProcessor.PostProcessGeneratedCode(schema, input);

        Assert.Contains("public ToolKind Kind", output);
        Assert.Contains("public ToolCallStatus Status", output);
    }

    [Fact]
    public void Rewrites_PermissionOptionKind_Placeholder()
    {
        var schema = new JsonSchema();
        var kind = new JsonSchema { DocumentPath = "#/definitions/PermissionOptionKind" };
        schema.Definitions["PermissionOptionKind"] = kind;

        var holder = new JsonSchema { Title = "Holder" };
        holder.Properties["kind"] = new JsonSchemaProperty { Reference = kind };
        schema.Definitions["Holder"] = holder;

        var input = """
        namespace Agent.Acp.Schema
        {
            public partial class Holder
            {
                [System.Text.Json.Serialization.JsonPropertyName("kind")]
                public Kind Kind { get; set; } = default!;
            }
        }
        """;

        var output = CodegenPostProcessor.PostProcessGeneratedCode(schema, input);
        Assert.Contains("public PermissionOptionKind Kind", output);
    }

    [Fact]
    public void Rewrites_PlanEntry_Enums_Placeholders()
    {
        var schema = new JsonSchema();
        var prio = new JsonSchema { DocumentPath = "#/definitions/PlanEntryPriority" };
        var status = new JsonSchema { DocumentPath = "#/definitions/PlanEntryStatus" };
        schema.Definitions["PlanEntryPriority"] = prio;
        schema.Definitions["PlanEntryStatus"] = status;

        var holder = new JsonSchema { Title = "Holder" };
        holder.Properties["priority"] = new JsonSchemaProperty { Reference = prio };
        holder.Properties["status"] = new JsonSchemaProperty { Reference = status };
        schema.Definitions["Holder"] = holder;

        var input = """
        namespace Agent.Acp.Schema
        {
            public partial class Holder
            {
                [System.Text.Json.Serialization.JsonPropertyName("priority")]
                public Priority Priority { get; set; } = default!;

                [System.Text.Json.Serialization.JsonPropertyName("status")]
                public Status Status { get; set; } = default!;
            }
        }
        """;

        var output = CodegenPostProcessor.PostProcessGeneratedCode(schema, input);
        Assert.Contains("public PlanEntryPriority Priority", output);
        Assert.Contains("public PlanEntryStatus Status", output);
    }

    [Fact]
    public void Rewrites_StopReason_To_ContextSpecific_Wrapper()
    {
        var schema = new JsonSchema();
        var stop = new JsonSchema { DocumentPath = "#/definitions/StopReason" };
        schema.Definitions["StopReason"] = stop;

        var holder = new JsonSchema { Title = "Holder" };
        holder.Properties["stopReason"] = new JsonSchemaProperty { Reference = stop };
        schema.Definitions["Holder"] = holder;

        var input = """
        namespace Agent.Acp.Schema
        {
            public partial class Holder
            {
                [System.Text.Json.Serialization.JsonPropertyName("stopReason")]
                public StopReason StopReason { get; set; } = default!;
            }
        }
        """;

        var output = CodegenPostProcessor.PostProcessGeneratedCode(schema, input);
        Assert.Contains("public PromptResponseStopReason StopReason", output);
        Assert.DoesNotContain("public StopReason StopReason", output);
    }

    [Fact]
    public void DoesNothing_When_No_Target_Definitions_Present()
    {
        var schema = new JsonSchema();

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

        Assert.Equal(input, output);
    }

    [Fact]
    public void Is_Idempotent()
    {
        var schema = new JsonSchema();
        schema.Definitions["ContentBlock"] = new JsonSchema { Title = "ContentBlock" };

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
