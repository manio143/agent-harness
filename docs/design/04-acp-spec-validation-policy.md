# ACP Spec Validation Policy

This library is an **agent harness**: its job is to make it hard for agent implementers to accidentally violate ACP.

We therefore prefer **hard validation** (fail fast) over permissive behavior when the spec requirements are about correctness.

## Validation Rules

### 1) MUST / MUST NOT
If the ACP docs say **MUST** or **MUST NOT**, we enforce it.

- Violations are treated as protocol/contract errors and typically return **JSON-RPC `-32602` (Invalid params)**.
- Unsupported optional methods return **`-32601` (Method not found)**.
- Requests made before `initialize` return **`-32000`**.

### 2) SHOULD / SHOULD NOT
If the ACP docs say **SHOULD**, we decide based on the intent.

We enforce **SHOULD** statements when:
- the guidance is effectively about correctness/consistency, and
- leaving it permissive would create an easy footgun for agent implementers.

We do **not** enforce **SHOULD** statements when:
- the guidance is purely UX/presentation (ordering, icons, placement), or
- it exists primarily to allow forward compatibility.

### 3) UX / presentation guidance
UX guidance is not required for correctness. We avoid hard validation for these areas.

### 4) Forward-compatibility guidance
When the spec says clients/agents must handle unknown values gracefully (e.g., categories), we avoid rejecting unknowns.

## Examples

### Enforce (footgun prevention)
- `session/set_config_option` should return complete configuration state (prevents drifting state).
- `currentValue` must be a valid option value (prevents impossible UI state).
- Transition-era sync between `modes` and `configOptions(category=mode)` (prevents inconsistent behavior).

### Do not enforce (UX only)
- Preferred ordering of config options (we preserve but do not reject on it).
- Unknown option categories (must be handled gracefully).
