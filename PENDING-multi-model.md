# PENDING — Multi-model support (friendly names + per-thread model)

Branch/target: `main`

## Goal
Enable the harness to use **different OpenAI-compatible model endpoints** for different tasks.

Key behaviors:
1) Config supports multiple models indexed by **friendly name** (e.g. `"granite"` → `{ baseUrl, apiKey, model }`).
2) Config defines:
   - `DefaultModel` (friendly name)
   - `QuickWorkModel` (friendly name) — used for non-main work like title generation.
3) `CallModel` effect is parameterized with a friendly name (or `"default"`).
4) Threads can change their inference model via:
   - Observed event: `ObservedSetModel`
   - Committed event: `SetModel`
   - Last `SetModel` in thread controls which model is used for subsequent `CallModel` effects.
5) If a requested model name does not exist, **fallback to DefaultModel**.
6) New tool: `thread_config`:
   - Required: `threadId`
   - Optional: `model` — sets the model for that thread.
   - Without `model`, returns projected thread config (at least: current model).
7) ✅ Unified thread creation tool: `thread_start` with `context: "new"|"fork"` and optional `model` (legacy `thread_new`/`thread_fork` removed).
8) ✅ ACP slash command: `/set-model <friendlyName>` — sets model for **main** thread.
9) ✅ System prompt advertises available models and default; prompt rendering emits: `Inference model has been set to: {model}.`

---

## Non-goals (for this iteration)
- No automatic model selection heuristics.
- No dynamic per-tool model routing beyond the explicit per-thread setting and the QuickWork title generation.
- No breaking change to existing configs (we will support migrating from the current single `OpenAI` block).

---

## Proposed design

### A) Configuration
**Current:** `AgentServerOptions.OpenAI` = single model.

**New:** `AgentServerOptions.Models` + pointers.

```csharp
public sealed class AgentServerOptions
{
  public ModelsOptions Models { get; set; } = new();
  // ... existing Sessions/Logging/Core/Acp

  public sealed class ModelsOptions
  {
    public Dictionary<string, OpenAiModelOptions> Catalog { get; set; } = new();
    public string DefaultModel { get; set; } = "default";
    public string QuickWorkModel { get; set; } = "default";

    // Back-compat: still allow binding from the old OpenAI block
    public OpenAiModelOptions OpenAI { get; set; } = new();
  }

  public sealed class OpenAiModelOptions
  {
    public string BaseUrl { get; set; } = "http://ollama-api:11434/v1";
    public string Model { get; set; } = "qwen2.5:3b";
    public string ApiKey { get; set; } = "ollama";
    public int? NetworkTimeoutSeconds { get; set; }
  }
}
```

**Binding rules (implementation detail):**
- If `Models.Catalog` is empty, synthesize `Catalog["default"] = Models.OpenAI`.
- `DefaultModel` and `QuickWorkModel` must refer to friendly names; if not found, fallback to `"default"`.

### B) Chat client selection
Introduce a small factory owned by Agent.Server composition root:

- `IChatClientFactory.Get(string friendlyNameOrDefault)`
  - resolves friendly name → model options
  - falls back to DefaultModel
  - returns a configured `IChatClient` (OpenAI-compatible via MEAI)

Title generation uses `QuickWorkModel`.

### C) Thread model state
Add events:
- Observed: `ObservedSetModel(threadId, model)`
- Committed: `SetModel(threadId, model)`

Projection rule:
- `ThreadModel` = last `SetModel` in committed stream (if none, null → default).

Reducer rules:
- On `ObservedSetModel`, commit `SetModel`.
- `Core.RenderPrompt` includes a short system line when it sees `SetModel`:
  - `Inference model has been set to: {model}.`

### D) CallModel effect
Change `CallModel` effect:
- from `new CallModel()`
- to `new CallModel(model: "default")` or `new CallModel(model: resolvedFriendlyName)`

Reducer:
- Wherever reducer emits `CallModel`, it should pick the resolved thread model:
  - if thread has SetModel → use that
  - else use `"default"`

Executor:
- `AcpEffectExecutor` resolves `CallModel.Model` to an actual chat client:
  - if model missing → fallback to DefaultModel

### E) Tools + ACP commands

#### `thread_config`
- Implemented in `AcpEffectExecutor.ExecuteToolAsync`.
- Reads/writes thread model via orchestrator interfaces:
  - use `IThreadObserver.ObserveAsync(threadId, ObservedSetModel(...))`

Return shape (MVP):
```json
{ "threadId": "thr_x", "model": "granite" }
```

#### `thread_start`
Replace `thread_new`/`thread_fork` tool schemas with one: ✅ done (legacy tools removed)
- `context`: `"new"|"fork"`
- `message`: string
- `delivery`: immediate|enqueue (existing)
- optional `model`: string

Behavior:
- context=new: seed from empty + message
- context=fork: seed from parent committed snapshot + message
- if `model` provided: enqueue an `ObservedSetModel` for the child thread before running its first turn (or commit `SetModel` during creation).

#### `/set-model`
ACP command exposed by `HarnessAcpSessionAgent` command parser:
- `/set-model granite`
  - converts into `ObservedSetModel(ThreadIds.Main, "granite")`

---

## TDD plan (incremental, green after each step)

### Step 1 — Configuration objects + resolution
1. Add new options types + binding fallback from old `OpenAI`.
2. Add unit tests in `Agent.Server.Tests` (create if missing) for:
   - empty catalog → `default` entry synthesized
   - invalid DefaultModel → fallback to `default`

### Step 2 — Chat client factory
1. Implement `ChatClientFactory` with `Get(modelNameOrDefault)`.
2. Unit tests:
   - requesting unknown model returns DefaultModel client
   - QuickWorkModel uses configured model

### Step 3 — Events: ObservedSetModel + SetModel
1. Add event records in `ObservedChatEvents.cs` + `SessionEvents.cs`.
2. Update `CoreReducer`:
   - handle `ObservedSetModel` → commit `SetModel`
3. Tests:
   - reducer commits `SetModel` and includes it in `NewlyCommitted`

### Step 4 — Prompt rendering for SetModel
1. Update `CoreReducer.RenderPrompt` to render SetModel → system message.
2. Tests:
   - committed SetModel renders `Inference model has been set to: X.`

### Step 5 — Parameterize CallModel
1. Update `Effects.cs` to make `CallModel` carry `Model`.
2. Update reducer emit sites to pass resolved model (`default` or last SetModel).
3. Tests:
   - when SetModel exists, subsequent observation causing model call emits `CallModel("granite")`
   - when none exists, emits `CallModel("default")`

### Step 6 — Execute CallModel with selected model
1. Update `AcpEffectExecutor` to accept `IChatClientFactory` (or a resolver) instead of a single `_chat`.
2. Ensure it logs prompt/tool decls unchanged.
3. Tests:
   - effect executor calls the correct client based on `CallModel.Model`
   - unknown model falls back to default

### Step 7 — QuickWork title generation uses QuickWorkModel
1. Replace `SessionTitleGenerator` injection so it gets a chat client from factory using QuickWorkModel.
2. Tests:
   - title gen uses quickwork client (spy chat client)

### Step 8 — `thread_config` tool
1. Add ToolSchema + implement in `AcpEffectExecutor`.
2. Tests:
   - reads current model
   - setting model commits SetModel in that thread (via ObserveAsync + quiescence)

### Step 9 — `thread_start` tool
1. Add new schema + keep `thread_new`/`thread_fork` temporarily as aliases (or delete once tests + samples updated).
2. Implement:
   - context=new
   - context=fork
   - optional model
3. Integration tests:
   - starting child with `model=granite` results in SetModel committed in child

### Step 10 — `/set-model` ACP slash command
1. Extend ACP input parsing in `HarnessAcpSessionAgent`.
2. Tests:
   - slash command produces `ObservedSetModel(main, X)` and commits SetModel

### Step 11 — External system prompt inputs: advertise model catalog
1. Add a small system message in CallModel prompt building listing:
   - available friendly model names
   - default model
   - quickwork model
2. Tests:
   - prompt contains the model list line(s)

### Step 12 — Samples + docs
1. Update samples/prompts to use `/set-model` and/or `thread_start`.
2. Ensure `dotnet test MarianAgent.slnx -c Release` and samples run.

---

## Clarifications (confirmed)
1) `thread_start(context="fork")` forks from the **current thread** only (same as today’s `thread_fork`). No `parentThreadId` parameter.
2) `thread_config.model`:
   - if present, it must be **non-null, non-empty** and either a **valid friendly model name** from config or the literal **`"default"`**.
   - no "clear/unset" operation via this tool.
3) Project the thread’s current model into **thread metadata** and surface it via `thread_list`.
