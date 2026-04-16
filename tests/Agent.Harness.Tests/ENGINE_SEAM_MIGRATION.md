# Engine seam migration (ACP JSON-RPC → direct session agent)

These tests were originally written as ACP JSON-RPC transport integrations (InMemoryTransport + AcpAgentServer + session/update parsing).

As the architecture converged on **ACP as projection-only** and the **ThreadOrchestrator as the single drain-to-quiescence engine**, most of these became *engine behavior* tests, not ACP contract tests.

They were migrated to the "engine seam": drive `HarnessAcpSessionAgent` directly with a scripted `IChatClient`, capture tool `rawOutput` via a custom `IAcpPromptTurn`, and assert against tool outputs + committed thread logs (`JsonlThreadStore`).

## Mappings

| Old (ACP JSON-RPC transport) | New (engine seam) |
|---|---|
| `AcpChildThreadOrchestrationIntegrationTests` | `EngineChildThreadOrchestrationIntegrationTests` |
| `AcpEnqueueWakeRegressionIntegrationTests` | `EngineEnqueueWakeRegressionIntegrationTests` |
| `AcpChildThreadIdleNotificationIntegrationTests` | `EngineChildThreadIdleNotificationIntegrationTests` |
| `AcpChildThreadIdleNotificationMainLogIntegrationTests` | `EngineChildThreadIdleNotificationMainLogIntegrationTests` |
| `AcpThreadListIntegrationTests` | `EngineThreadListIntegrationTests` |

## ACP contract tests that remain transport-level

These should stay at the ACP layer because they validate public protocol / projection behavior:
- `AcpStreamingRegressionIntegrationTests`
- `AcpToolKindPublishingIntegrationTests`

