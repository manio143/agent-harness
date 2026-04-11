# ACP Harness (C#) — Model Quality, Postprocess Rules, and Decisions

## Context

We generate C# DTOs from ACP’s release-pinned JSON Schema.
In practice, NJsonSchema generates most types, but it has several known gaps:

- Discriminated unions (`oneOf` + discriminator) are not modeled end-to-end.
- `$ref` targets sometimes become *context-less placeholder types* (`Kind2`, `Status3`, `Priority`, etc.).
- Some union or ref surfaces degrade into placeholder extension objects (`Content1`, `Outcome`, `Update`).
- Array item refs may use placeholder element types (`ICollection<Content1>`, `ICollection<ConfigOptions>`).

The goal is a regen-safe pipeline: delete `Generated/`, rerun generators, compile.

## Options considered

### Option A — Manual patches to generated code
Rejected.
- Not regeneratable.

### Option B — External patch step (Python)
Rejected.
- Adds a non-.NET dependency and is easy to drift.

### Option C — Deterministic .NET postprocess inside the generator (selected)
Selected.
- One .NET pipeline.
- Deterministic, schema-driven rewrites.
- TDD at the rewrite boundary.

## Selected approach (current)

- **NJsonSchema** generates the bulk DTO file: `src/Agent.Acp/Generated/AcpSchema.g.cs`.
- `tools/Agent.Acp.TypeGen` applies a deterministic postprocess step (`CodegenPostProcessor`).
- **UnionGen** generates discriminated unions (inheritance + `JsonConverter` dispatch):
  - `ContentBlock`
  - `SessionUpdate` (with distinct chunk update derived types)
  - `ToolCallContent`

Unknown/extension union variants are represented as `Unknown*` wrappers over `JsonElement`.

## Rewrite rules (schema-driven)

The postprocessor identifies affected properties by scanning the schema JSON for `$ref` patterns.
Two categories:

### 1) Direct refs (`$ref` / `allOf`)
Rewrite property type tokens when the schema property references a target `$defs.<X>`.

Examples (current set):
- `ContentBlock` refs: `Content\d+` → `ContentBlock`
- `SessionUpdate` refs: `Update\d*` → `SessionUpdate`
- Permission outcomes: `Outcome\d*` → `RequestPermissionOutcome` / `SelectedPermissionOutcome`
- Tool calls:
  - `ToolKind` refs: `Kind\d*` → `ToolKind`
  - `ToolCallStatus` refs: `Status\d*` → `ToolCallStatus`
- Permissions:
  - `PermissionOptionKind` refs: `Kind\d*` → `PermissionOptionKind`
- Plan entries:
  - `PlanEntryPriority` refs: `Priority` → `PlanEntryPriority`
  - `PlanEntryStatus` refs: `Status\d*` → `PlanEntryStatus`
- Stop reason:
  - `StopReason` refs: `StopReason\d*` → `StopReason`

### 2) Array item refs (`items.$ref`)
Rewrite the element type for `ICollection<T>` properties (and their initializer types) when the schema says
`items.$ref` targets a known `$defs.<X>`.

Examples:
- `items: ContentBlock` → `ICollection<ContentBlock>` (and `new Collection<ContentBlock>()`)
- `items: SessionConfigOption` → `ICollection<SessionConfigOption>` (and initializer)
- `items: ToolCallContent` → `ICollection<ToolCallContent>`

## Quality gates

We keep a small test suite that fails if placeholders leak back into the public generated surface.

Current gate: `GeneratedModelQualityGateTests` ensures `AcpSchema.g.cs` does not contain known placeholder patterns
(e.g. `public Content\d+ ...`, `public Outcome Outcome`, `public Update Update`, `ICollection<content>`, `StopReason2`).

## Decisions recorded (from Marian)

- **Model surface vision:** decent unions + optionality close to C# best practices.
- **Unknowns:** prefer `JsonElement` wrappers for forward compatibility.
- **Patch file policy:** keep `AcpSchema.Patches.cs` present, but prefer it to be empty; only allow placeholder types there as a last resort.
- **Quality gates:** keep them targeted (avoid noisy overreach).
