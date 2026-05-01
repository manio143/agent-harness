# PowerShell MCP Proxy Cmdlets (via `agent_shell_execute`)

## Status
Draft (approved direction by Marian, 2026-05-01)

## Goals
Expose MCP-provided tools as PowerShell commands inside the agent-internal PowerShell shell (`agent_shell_execute`).

**Primary UX goals:**
- Commands should look and feel close to native PowerShell cmdlets.
- Parameter binding should be PowerShell-native (typed parameters, `-Name Value`, `-Switch`, arrays, hashtables).
- Tool results should return structured objects by default.

**Platform goals:**
- Works with the in-process PowerShell runspace used by `agent_shell_execute`.
- Uses existing MCP infrastructure (invoker, tool catalog) to dispatch calls.
- Capability-aware: only generate commands for tools present in the offered tool catalog for the thread.

## Non-goals
- Implementing dynamic .NET cmdlets (Reflection.Emit / Roslyn). We start with PowerShell proxy functions.
- Directory listing / filesystem features beyond what MCP provides.
- Full fidelity JSON Schema → PowerShell parameter mapping on day one.

## Background / Current State
- The harness exposes an agent-internal tool `agent_shell_execute` backed by an in-process PowerShell Core runspace.
- The shell has an agent-local working drive `sandbox:` rooted at session `pwsh_work`.
- The shell optionally provides a `client:` PSDrive backed by ACP fs operations (read/write), rooted at ACP session cwd.
- MCP tools exist in the offered tool catalog (server namespaced with `{server}__{tool}`), and are invoked via a harness MCP invoker.

## High-level Approach
We will generate and auto-import PowerShell modules that define **proxy functions** for MCP tools.

At runtime (shell init and/or MCP discovery), the harness:
1. Obtains the MCP tool catalog visible in the current thread (after thread capability filtering).
2. Groups tools by MCP server name.
3. Generates a PowerShell module per server containing:
   - One proxy function per tool, named using a PowerShell cmdlet-ish convention.
   - A shared helper function (per module or global) to dispatch to the MCP invoker.
4. Imports these modules into the runspace (auto-import).

The proxy functions collect bound parameters (PowerShell types), translate them into a JSON-compatible argument map, and invoke the underlying MCP tool.

## Naming & Cmdlet Shape
### Inputs
Tool names in MCP are snake_case. Tool names are namespaced as:
- `server__tool_name`

### Name mapping
Given `tool_name` (snake_case):

1) Detect whether the first segment is a known PowerShell verb.
- If the first segment is in `ApprovedVerbs` (curated list), treat it as the verb.
  - Example: `get_work_items` → Verb=`Get`, Noun=`WorkItems` → `Get-WorkItems`
- Otherwise, use `Invoke` as the verb.
  - Example: `advanced_copy` → `Invoke-AdvancedCopy`

2) Convert the remaining segments to PascalCase and concatenate.

### Server prefixing (conflict resolution)
Default: do **not** include the server name.

If a generated cmdlet name conflicts (same name from multiple servers):
- Insert server name **after verb** as a prefix for the noun.
  - `Get-<Server><Noun>`
  - Example conflict: both `jira__get_issue` and `azure__get_issue` would produce `Get-Issue`.
    Resolution: `Get-JiraIssue` and `Get-AzureIssue`.

### Fallback / escape hatch
Always provide a generic entrypoint (global module):
- `Invoke-McpTool -Server <server> -Name <tool_name> -Args <hashtable> [-RawOutput]`

This is the escape hatch for:
- tools that cannot be mapped nicely
- complex schemas
- debugging

## Parameters & Validation
### Schema source
We use the MCP tool input schema (JSON Schema-like) as the source of truth.

### Mapping rules
- `type: string` → `[string]`
- `type: integer` → `[int]` (or `[long]` if needed later)
- `type: number` → `[double]`
- `type: boolean` → `[switch]` (or `[bool]` when a tri-state is needed)
- `type: array` → `T[]` if item type is known else `[object[]]`
- `type: object` → `[hashtable]` (with per-property parameters when shape is known)

### Required vs optional behavior (balanced strictness)
- **Required** properties are hard validated:
  - parameter is mandatory
  - type conversion must succeed
  - missing values fail fast with a clear error
- **Optional** properties are treated more loosely:
  - allow omission
  - allow broader `[object]` types when schema is ambiguous
  - include `-RawArgs [hashtable]` to pass additional properties (merged; explicit parameters win)

### Enums
- When `enum` is present and values are strings, emit `ValidateSet`.

## Return Types
Default behavior: return structured objects.

- If the MCP result is JSON:
  - Convert to `PSCustomObject` / `Hashtable` recursively.
- If the MCP result is a scalar:
  - Return scalar.

Add switch:
- `-RawOutput`: return raw JSON (string) instead of structured conversion.

## Module Model
### Module per MCP server
Generate one module per server:
- `Mcp.<server>`

Auto-import all modules that are applicable for the thread.

### Global helper module
Additionally, generate/import a small helper module:
- `Mcp.Core`

`Mcp.Core` provides:
- `Invoke-McpTool`
- shared serialization helpers

## Capability Gating
- Only generate functions for tools present in `OfferedToolNames`.
- Tools that are not in the catalog must not be callable from the shell.

## Lifecycle / Refresh
We need to handle MCP tool discovery timing (tools may appear after session start).

Refresh triggers:
- On shell init: generate/import based on current catalog.
- When MCP tool catalog changes (new servers/tools): regenerate.

Design: prefer idempotent regeneration (reimport) and allow `Import-Module -Force`.

## Error Handling
- If an MCP invocation fails:
  - throw a PowerShell terminating error with the MCP error details
  - include tool name + server in message

## Security / Safety Notes
- This does not increase raw execution authority beyond what MCP already exposes.
- However it increases usability; treat availability as capability-gated.
- Ensure no hidden invocation path exists for tools not in offered catalog.

## Testing Strategy
### Deterministic tests (harness-level)
- Given a tool catalog with tool `get_work_items`:
  - generator produces `Get-WorkItems` in server module
- Given tool `advanced_copy`:
  - generator produces `Invoke-AdvancedCopy`
- Required parameter enforced:
  - calling without mandatory arg fails
- Conflict resolution:
  - `jira__get_issue` and `azure__get_issue` → `Get-JiraIssue`, `Get-AzureIssue`
- `-RawOutput` returns JSON string
- Default returns object

### Integration tests (shell-level)
- Import generated modules in runspace
- Invoke a proxy function, ensure it calls MCP invoker with the expected tool name and args

### Sample
Add a sample demonstrating:
- a fake MCP server with one tool exposing a known-verb name
- a second tool requiring fallback naming
- run via `acpx` and end with `DONE`

## Implementation Plan (Phase 1 + Phase 2)
### Phase 1
- Implement name mapping + per-server module generation (proxy functions).
- Implement `Invoke-McpTool` bridge.
- Auto-import modules in `agent_shell_execute` runspace init.

### Phase 2
- Improve parameter typing/validation:
  - mandatory for required
  - ValidateSet for enums
  - better object coercion
- Improve conflict detection and server prefixing.
- Improve help strings and discoverability (`Get-Command`, `Get-Help`).

## Open Decisions
1) Approved verb list source:
   - hard-coded list in harness (curated)
   - derived from PowerShell `Get-Verb`

2) Where to host the generator:
   - C# generates PS script text and imports it
   - ship a reusable PS module template with placeholders

3) JSON conversion policy:
   - strict schema-based conversion
   - best-effort conversion with pass-through
