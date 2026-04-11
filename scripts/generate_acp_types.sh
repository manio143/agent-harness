#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SCHEMA_FILE="$ROOT_DIR/schema/schema.codegen.json"
OUT_DIR="$ROOT_DIR/src/Marian.Agent.Acp/Generated"
OUT_FILE="$OUT_DIR/AcpSchema.g.cs"

mkdir -p "$OUT_DIR"

dotnet tool restore >/dev/null

# NOTE: We keep NSwag as a local tool, but the CLI's jsonschema2csclient generator
# only emits the root type for ACP's definition-heavy schema. So we generate via
# NJsonSchema (the same underlying library NSwag uses) to ensure all $defs are emitted.

dotnet run --project "$ROOT_DIR/tools/Marian.Agent.Acp.TypeGen" -- \
  "$SCHEMA_FILE" \
  "$OUT_FILE" \
  "Marian.Agent.Acp.Schema"

echo "Generated: $OUT_FILE"
