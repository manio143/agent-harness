# Observed vs Committed Events (MEAI-first, ACP-friendly)

## Context / Problem

We want an agent harness with a **functional core** and an **imperative shell**.

- The model boundary (Microsoft.Extensions.AI / MEAI) supports **streaming** via `IAsyncEnumerable<ChatResponseUpdate>`.
- ACP supports streaming to clients via `session/update` notifications (e.g., `AgentMessageChunk`), followed by a final `session/prompt` response.
- We may want unconventional semantics later (rewrite history, buffer then redact, fork/replay, etc.).

The key architectural question is how to represent streaming model output inside the harness without:
- coupling the core to provider-specific details,
- leaking uncommitted content to the user,
- losing information necessary for future features.

## MEAI streaming reality (source-backed)

MEAI `IChatClient` defines:
- `Task<ChatResponse> GetResponseAsync(...)`
- `IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(...)`

`ChatResponseUpdate` represents a single streamed update that *layers* to form a full response, and includes fields like `Text`, `Contents`, `Role`, and `FinishReason`.

Sources:
- `IChatClient` interface (dotnet/extensions):
  - https://raw.githubusercontent.com/dotnet/extensions/68b25aeb2d752273e1d5621b38a7869ce63970c3/src/Libraries/Microsoft.Extensions.AI.Abstractions/ChatCompletion/IChatClient.cs
- `ChatResponseUpdate` reference (Microsoft Learn):
  - https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.ai.chatresponseupdate

**Boundary signal:** MEAI updates may include a `FinishReason`, and independent of that, the stream completion is a hard boundary.

## Semantic Kernel streaming reality (useful contrast)

Semantic Kernel’s agent streaming documentation notes that, although response content may be streamed incrementally, chat history is updated after the full response is received.

Source:
- https://raw.githubusercontent.com/MicrosoftDocs/semantic-kernel-docs/main/semantic-kernel/Frameworks/agent/agent-streaming.md

This aligns with the idea of buffering until a commit point, even if streaming is shown to users.

## Design principle

**Observed** events represent what the model boundary produced (streaming updates).

**Committed** events represent stable session facts that:
- are safe to publish to the client (ACP `session/update`),
- are safe to include in the next model prompt (history),
- are replayable and deterministic.

Only committed events are used by the publishing layer.

## Why not feed MEAI `ChatResponseUpdate` directly into the core?

It is tempting, but has downsides:

1. **Coupling:** It couples the functional core directly to MEAI’s object model and its evolution.
2. **Provider variability:** Different providers populate different subsets of `ChatResponseUpdate` (esp. `RawRepresentation`). Core would become full of conditional logic.
3. **Testing ergonomics:** Reducer tests become verbose/brittle if they must construct `ChatResponseUpdate` objects for every scenario.

However, the counter-argument is valid: **normalization must not lose information**.

## Compromise: lossless observed events

We introduce a small, stable set of harness-owned **ObservedChatEvent** types.

Crucially:
- Observed events MUST preserve the underlying MEAI update for future debugging/enrichment.
- The core operates on the normalized fields for deterministic logic.

Example shape (illustrative, not final API):

```csharp
public abstract record ObservedChatEvent
{
    // Lossless attachment for debugging/provider-specific enrichments.
    public required object RawUpdate { get; init; }
}

public sealed record ObservedAssistantTextDelta : ObservedChatEvent
{
    public required string Text { get; init; }
}

public sealed record ObservedToolCallDelta : ObservedChatEvent
{
    public required string CallId { get; init; }
    public string? Name { get; init; }
    public string? ArgumentsDelta { get; init; }
}

public sealed record ObservedMessageCompleted : ObservedChatEvent
{
    public string? FinishReason { get; init; }
}
```

### Boundary handling

The MEAI adapter MUST produce `ObservedMessageCompleted`:
- when a MEAI update includes a finish signal, OR
- when the upstream streaming sequence completes (synthetic completion).

This avoids heuristics inside the core.

## Session state shape (functional core)

We keep:
- an append-only committed event list
- in-flight buffers in state (not in the committed log)

Example:

```csharp
public sealed record SessionState(
    ImmutableArray<SessionEvent> Committed,
    TurnBuffer Buffer);

public sealed record TurnBuffer(
    string AssistantText,
    bool AssistantMessageOpen);
```

The reducer updates `Buffer` on deltas, and commits stable events only at boundaries.

## Committed events (publishable)

Committed events are strongly typed and represent stable facts:
- `UserMessageAdded(text)`
- `AssistantMessageAdded(text)`

Optionally later:
- `AssistantChunkCommitted(textChunk)` (incremental commit)
- tool call events, errors, etc.

Only committed events should be eligible for:
- ACP publishing (`session/update`)
- inclusion in the next model prompt

## Publishing policy

Streaming to users is controlled by *what the core commits*:
- If the core only commits on completion: users see only final content.
- If the core commits incrementally: users see incremental `session/update` chunks.

ACP is a transport; it must publish only committed events.

## Test strategy

- Core reducer tests use small, stable `ObservedChatEvent` inputs (easy, deterministic).
- Adapter tests validate mapping:
  - MEAI `ChatResponseUpdate` -> `ObservedChatEvent`
  - committed events -> ACP `session/update` notifications

## Key decisions

1. **Observed vs committed separation** is mandatory.
2. Observed events must be **lossless** by carrying the raw MEAI update.
3. The core commits only when it decides content is releasable.
4. ACP publishing uses only committed events.
