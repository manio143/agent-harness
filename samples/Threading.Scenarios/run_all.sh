#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_DIR="$(cd "$ROOT_DIR/../.." && pwd)"
OUT_DIR="$ROOT_DIR/out"

mkdir -p "$OUT_DIR"

cd "$REPO_DIR"

echo "[build] Building Agent.Server (Release)" | tee "$OUT_DIR/build.log"
dotnet build src/Agent.Server/Agent.Server.csproj -c Release | tee -a "$OUT_DIR/build.log"

AGENT_CMD="dotnet src/Agent.Server/bin/Release/net8.0/Agent.Server.dll"

# Make RPC logging opt-in (can be noisy). Set to true in environment to debug.
: "${LOG_RPC:=false}"
if [[ "$LOG_RPC" == "true" ]]; then
  export AGENTSERVER_AgentServer__Logging__LogRpc=true
fi

run_scenario() {
  local name="$1"
  echo "" | tee "$OUT_DIR/${name}.log"
  echo "========== $name ==========" | tee -a "$OUT_DIR/${name}.log"
  bash "$ROOT_DIR/${name}.sh" "$AGENT_CMD" 2>&1 | tee -a "$OUT_DIR/${name}.log"
}

run_scenario "scenario1_child_immediate_persisted"
run_scenario "scenario2_enqueue_idle_ordering"
run_scenario "scenario3_self_send_enqueue_no_deadlock"

echo "" | tee -a "$OUT_DIR/summary.log"
echo "All scenarios completed. Logs in: $OUT_DIR" | tee -a "$OUT_DIR/summary.log"
