#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_DIR="$(cd "$ROOT_DIR/../.." && pwd)"
OUT_DIR="$ROOT_DIR/out"

mkdir -p "$OUT_DIR"

cd "$REPO_DIR"

cleanup_acpx_stale_locks() {
  # Best-effort: remove stale queue-owner locks/sockets.
  # Mode:
  # - dead (default): remove only when pid is dead
  # - all: kill ALL queue-owners referenced by lock files (useful if acpx is wedged)
  local mode="${ACPX_CLEANUP_MODE:-dead}"

  local qdir="$HOME/.acpx/queues"
  if [[ ! -d "$qdir" ]]; then
    return 0
  fi

  shopt -s nullglob
  for lock in "$qdir"/*.lock; do
    local pid socket
    pid="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1])).get("pid",""))' "$lock" 2>/dev/null || true)"
    socket="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1])).get("socketPath",""))' "$lock" 2>/dev/null || true)"

    if [[ -z "$pid" ]]; then
      continue
    fi

    if [[ "$mode" == "all" ]]; then
      kill -TERM "$pid" 2>/dev/null || true
      sleep 0.05
      kill -KILL "$pid" 2>/dev/null || true
      rm -f "$lock" 2>/dev/null || true
      if [[ -n "$socket" ]]; then
        rm -f "$socket" 2>/dev/null || true
      fi
      continue
    fi

    if ! kill -0 "$pid" 2>/dev/null; then
      rm -f "$lock" 2>/dev/null || true
      if [[ -n "$socket" ]]; then
        rm -f "$socket" 2>/dev/null || true
      fi
    fi
  done
  shopt -u nullglob
}

# Try to start clean.
cleanup_acpx_stale_locks

echo "[build] Building Agent.Server (Release)" | tee "$OUT_DIR/build.log"
dotnet build src/Agent.Server/Agent.Server.csproj -c Release | tee -a "$OUT_DIR/build.log"

AGENT_CMD="dotnet src/Agent.Server/bin/Release/net8.0/Agent.Server.dll"

# Default ACP timeouts (seconds) for acpx calls in scenarios.
: "${ACP_TIMEOUT:=600}"
export ACP_TIMEOUT

# Retry transient prompt failures (e.g. agent reconnect / local model flakiness).
: "${ACP_PROMPT_RETRIES:=2}"
export ACP_PROMPT_RETRIES

# Queue-owner idle TTL (seconds). Keeping this low prevents leaking long-lived agent processes.
: "${ACPX_TTL:=30}"
export ACPX_TTL

# Hard wall-time timeout (seconds) wrapping each acpx invocation to avoid indefinite hangs.
: "${ACPX_WALL_TIMEOUT:=240}"
export ACPX_WALL_TIMEOUT

# Make RPC logging opt-in (can be noisy). Set to true in environment to debug.
: "${LOG_RPC:=false}"
if [[ "$LOG_RPC" == "true" ]]; then
  export AGENTSERVER_AgentServer__Logging__LogRpc=true
fi

# Force a tool-capable local model for scenarios (some Ollama models reject tool usage).
# Defaults are chosen to reduce OOM risk; override via environment if needed.
: "${SCENARIO_DEFAULT_MODEL:=qwen}"
: "${SCENARIO_QUICKWORK_MODEL:=qwen}"
: "${SCENARIO_OPENAI_MODEL:=qwen2.5:3b}"

export AGENTSERVER_AgentServer__Models__DefaultModel="$SCENARIO_DEFAULT_MODEL"
export AGENTSERVER_AgentServer__Models__QuickWorkModel="$SCENARIO_QUICKWORK_MODEL"
# Back-compat (some components still read AgentServer:OpenAI)
export AGENTSERVER_AgentServer__OpenAI__Model="$SCENARIO_OPENAI_MODEL"

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
