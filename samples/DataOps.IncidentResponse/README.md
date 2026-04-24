# DataOps Incident Response - Multi-Turn Agent Evaluation Scenario

## Overview

This scenario simulates a **real-world DataOps incident response workflow** for evaluating the agent harness through manual log review. It exercises multi-turn reasoning, thread coordination, tool failure recovery, and large data handling across three distinct phases.

**Scenario Type:** Manual evaluation (NOT an automated test)  
**Complexity:** High - Multi-phase, multi-thread, with intentional failures  
**Evaluation Method:** Manual log review + automated validation scripts

## Narrative

**Incident:** Critical temperature spike detected on multiple environmental sensors in production data centers.

**Mission:** The agent must act as an incident responder, following a structured runbook to:
1. Validate sensor telemetry data (with graceful handling of malformed input)
2. Isolate and analyze affected sensors using parallel child threads
3. Generate a comprehensive incident report with timeline and mitigation steps

**Challenge Points:**
- Intentional data corruption requiring error recovery
- Multi-thread coordination for parallel analysis
- Tool budget constraints forcing efficient execution
- Large-ish datasets (>2KB) testing compaction behavior
- Precise tool parameter requirements (thread_start with mode, capabilities)

## Real-World Patterns (Research-Backed)

Based on [web research](https://www.braintrust.dev/articles/agent-evaluation) on agent evaluation:
- **Multi-step workflows:** Agent evaluation requires testing decision quality across multiple tool calls ([Braintrust: Agent Evaluation](https://www.braintrust.dev/articles/agent-evaluation))
- **Data pipeline validation:** Agentic AI in DataOps automates incident remediation through structured runbooks ([Acceldata: Agentic AI for DataOps](https://www.acceldata.io/blog/agentic-ai-for-dataops-from-alert-fatigue-to-fully-automated-incident-remediation))
- **Failure recovery:** Real-world agents must handle parsing errors, network timeouts, and incomplete data gracefully
- **Thread orchestration:** Complex scenarios require spawning specialized workers with constrained capabilities ([Medium: Multi-Step Evaluation](https://medium.com/@avigoldfinger/multi-step-evaluation-gaps-are-holding-back-ai-agents-lets-fix-them-together-4b25b101528f))

## Scenario Phases

### Phase 1: Data Validation (8-10 tool calls expected)
**Duration:** ~2-3 minutes  
**Tools Required:** `report_intent`, `read_text_file`, `write_text_file`, `execute_command`, `everything__echo`

**Objectives:**
- Load incident metadata (`data/incident.json`)
- Parse valid sensor data (`data/sensors.csv`)
- **INTENTIONAL FAILURE:** Attempt to parse `data/sensors_malformed.csv` (will fail)
- Recover gracefully: log error, continue with valid data
- Identify critical sensors (temp >30°C)
- Generate validation report (`out/phase1_validation.json`)

**Success Criteria:**
- ✅ Malformed data error detected and logged
- ✅ Agent continues execution after failure
- ✅ Validation JSON contains all required fields
- ✅ Critical sensors correctly identified (sensor-001, sensor-003)

### Phase 2: Multi-Thread Analysis (12-15 tool calls expected)
**Duration:** ~3-5 minutes  
**Tools Required:** `report_intent`, `thread_start`, `thread_read`, `thread_list`, `read_text_file`, `write_text_file`

**Objectives:**
- Create TWO child threads (single mode) for parallel sensor analysis
- Each child: restricted capabilities (only file I/O, no exec)
- Child A: Analyze sensor-001 statistics
- Child B: Analyze sensor-003 statistics
- Read results from both children using `thread_read`
- Cross-reference with `sensor_config.yaml` thresholds
- Generate comprehensive analysis (`out/phase2_analysis.md`)

**Success Criteria:**
- ✅ Both child threads created with correct `mode: "single"`
- ✅ Capability restrictions enforced (children can't use execute_command)
- ✅ Parent reads child outputs via `thread_read`
- ✅ Analysis includes statistics from BOTH sensors
- ✅ Recommendations based on threshold violations

### Phase 3: Incident Report (8-10 tool calls expected)
**Duration:** ~2-3 minutes  
**Tools Required:** `report_intent`, `read_text_file`, `write_text_file`, `everything__echo`

**Objectives:**
- Aggregate findings from Phase 1 & 2
- Document all tool calls executed
- Generate executive summary
- Create final incident report (`out/incident_report.md`)
- Use MCP checkpoint (`everything__echo`)

**Success Criteria:**
- ✅ Report includes timeline, findings, impact assessment
- ✅ Tool call history documented
- ✅ Executive summary present
- ✅ MCP tool successfully invoked

## Dataset

### Valid Data Files
- `data/sensors.csv` - 35 sensor readings (5 sensors × 7 timestamps) - ~2KB
- `data/incident.json` - Incident metadata with affected sensors
- `data/sensor_config.yaml` - Sensor thresholds and alert routing
- `data/runbook.md` - Detailed incident response procedures
- `data/validation_checklist.txt` - Phase 1 checklist template

### Malformed Data (Intentional)
- `data/sensors_malformed.csv` - Invalid data for testing error recovery:
  - Invalid numeric values ("invalid_temp", "MISSING", "NOT_A_NUMBER")
  - Completely malformed lines
  - Missing required fields
  - Empty sensor IDs

## Tool Coverage

This scenario exercises the following tools:

**Threading:**
- `thread_start` (single mode + multi mode)
- `thread_send` (optional, if agent chooses to enqueue follow-ups)
- `thread_read` (reading child outputs)
- `thread_list` (verifying thread creation)
- `thread_config` (optional, if agent queries capabilities)

**Host File System:**
- `read_text_file` (CSV, JSON, YAML, Markdown, TXT)
- `write_text_file` (validation outputs, analysis, reports, error logs)

**Host Execution:**
- `execute_command` (optional: grep, wc, sort for data processing)

**MCP Integration:**
- `everything__echo` (validation checkpoints)
- (Optionally: `everything__get_sum` if agent calculates sensor counts)

**Intent Tracking:**
- `report_intent` (phase transitions, error recovery)

## Running the Scenario

### Prerequisites
```bash
# Build the agent server
cd /home/node/.openclaw/workspace/marian-agent
dotnet build Agent.slnx -c Release

# Ensure acpx is available
which acpx || npm install -g acpx
```

### Execution

**Option 1: Run via scenario script (recommended)**
```bash
cd /home/node/.openclaw/workspace/marian-agent
bash samples/DataOps.IncidentResponse/run.sh "dotnet src/Agent.Server/bin/Release/net8.0/Agent.Server.dll"
```

**Option 2: Manual execution with acpx**
```bash
cd /home/node/.openclaw/workspace/marian-agent/samples/DataOps.IncidentResponse

# Configure MCP server (optional, for everything__ tools)
cat > .acpxrc.json <<'JSON'
{
  "agents": {
    "marian-agent": {
      "command": "dotnet /home/node/.openclaw/workspace/marian-agent/src/Agent.Server/bin/Release/net8.0/Agent.Server.dll"
    }
  },
  "defaultAgent": "marian-agent",
  "mcpServers": [
    {
      "type": "stdio",
      "name": "everything",
      "command": "npx",
      "args": ["-y", "-p", "ajv", "-p", "@modelcontextprotocol/server-everything", "mcp-server-everything", "stdio"]
    }
  ]
}
JSON

# Start session
npx acpx --cwd . sessions new --name dataops-eval

# Run each phase manually (copy prompts from run.sh)
npx acpx --cwd . prompt --session dataops-eval "<phase1_prompt>"
npx acpx --cwd . prompt --session dataops-eval "<phase2_prompt>"
npx acpx --cwd . prompt --session dataops-eval "<phase3_prompt>"
```

### Environment Variables

```bash
# Timeout configuration
export ACP_TIMEOUT=600              # ACP operation timeout (seconds)
export ACPX_TTL=300                 # Queue owner idle TTL (seconds)
export ACPX_WALL_TIMEOUT=720        # Hard wall-time limit (seconds)
export ACP_PROMPT_RETRIES=2         # Retry count for transient failures

# Model configuration (override defaults)
export SCENARIO_DEFAULT_MODEL=qwen
export SCENARIO_QUICKWORK_MODEL=qwen
```

## Evaluation Guide

### Where to Look in Logs

**Session Events:**
```bash
# Main thread events
cat .agent/sessions/<session_id>/threads/main/events.jsonl | jq -c 'select(.type=="tool_call_requested" or .type=="tool_call_completed")'

# Child thread events (Phase 2)
cat .agent/sessions/<session_id>/threads/analyze-sensor-001-*/events.jsonl
cat .agent/sessions/<session_id>/threads/analyze-sensor-003-*/events.jsonl
```

**acpx Stream Logs:**
```bash
# Real-time ACP protocol stream
tail -f ~/.acpx/sessions/<session_id>.stream.ndjson
```

**Scenario Output Logs:**
```bash
# When using run.sh
cat samples/DataOps.IncidentResponse/out/scenario.log
```

### What to Evaluate

**Turn-by-Turn Behavior:**
1. **Phase 1 Turn 1:** Did agent follow tool order? (report_intent → read → read → read → parse → write → echo)
2. **Malformed data recovery:** Did agent detect error, log it, and continue?
3. **Phase 2 Turn 1:** Were both `thread_start` calls correct? (mode="single", capabilities properly set)
4. **Thread coordination:** Did agent wait for children before reading? Used `thread_read` correctly?
5. **Phase 3 Turn 1:** Did agent aggregate all previous outputs?

**Tool Call Precision:**
- `thread_start` parameters: Verify `mode` field present (common failure point)
- `capabilities` structure: Check `deny` and `allow` arrays match requirements
- File paths: Relative vs absolute (agent should normalize to sample directory)
- MCP tool names: Correct `everything__` prefix

**Failure Recovery:**
- Error logged to `out/errors.log`?
- Agent reported intent for recovery? (`"intent": "recovered from malformed data"`)
- Execution continued without halting?

**Compaction Behavior:**
- Tool outputs >2KB may be compacted in subsequent turns
- Agent should re-read files if needed, not rely on in-context tool results

**Budget Adherence:**
- Phase 1: ≤15 tool calls
- Phase 2: ≤20 tool calls
- Phase 3: ≤15 tool calls
- Did agent stop if budget exceeded?

### Automated Validation

Each phase has a validation script:

```bash
# Run after scenario completion
bash samples/DataOps.IncidentResponse/scripts/validate_phase1.sh
bash samples/DataOps.IncidentResponse/scripts/validate_phase2.sh
bash samples/DataOps.IncidentResponse/scripts/validate_phase3.sh
```

**Validation Checks:**
- **Phase 1:** JSON schema, critical sensor identification, status counts
- **Phase 2:** Markdown structure, statistics presence, word count (>100)
- **Phase 3:** Report sections, tool documentation, executive summary (>200 words)

## Scoring Rubric

### Execution Quality (40 points)
- [ ] All 3 phases completed (10 pts)
- [ ] No runtime errors/exceptions (10 pts)
- [ ] Stayed within tool budgets (10 pts)
- [ ] Correct tool sequence per phase (10 pts)

### Tool Precision (30 points)
- [ ] `thread_start` with correct `mode` parameter (10 pts)
- [ ] Capability restrictions properly set (5 pts)
- [ ] File I/O paths correctly resolved (5 pts)
- [ ] MCP tools invoked successfully (5 pts)
- [ ] `report_intent` used at phase boundaries (5 pts)

### Error Recovery (15 points)
- [ ] Malformed data error detected (5 pts)
- [ ] Error logged to file (5 pts)
- [ ] Execution continued gracefully (5 pts)

### Output Quality (15 points)
- [ ] Phase 1 validation JSON well-formed (5 pts)
- [ ] Phase 2 analysis substantive (>100 words) (5 pts)
- [ ] Phase 3 report comprehensive (>200 words) (5 pts)

**Total: 100 points**

**Grading:**
- **90-100:** Excellent - Production-ready agent behavior
- **75-89:** Good - Minor issues, mostly correct
- **60-74:** Acceptable - Some failures, but completed core tasks
- **<60:** Needs improvement - Major failures or incomplete execution

## Common Failure Modes

Based on existing test patterns:

1. **Missing `mode` parameter in `thread_start`** → Agent retries with corrected call
2. **Incorrect capability syntax** → Child threads fail to spawn
3. **Path resolution issues** → Files not found (relative vs absolute)
4. **Premature tool budget exhaustion** → Agent stops mid-phase
5. **Not waiting for child threads** → `thread_read` returns incomplete data
6. **MCP tool name errors** → Missing `everything__` prefix
7. **JSON syntax errors in structured outputs** → Validation fails

## Extensions

To make the scenario more challenging:

- **Add Phase 4:** Require agent to send follow-up tasks to child threads using `thread_send`
- **Increase data volume:** Expand `sensors.csv` to 500+ rows to force compaction
- **Add time constraints:** Require completion within strict SLA (e.g., 5 minutes total)
- **Multi-mode threading:** Use `mode: "multi"` for interactive debugging sessions
- **Network tool failures:** Simulate transient MCP connection errors

## Files

```
DataOps.IncidentResponse/
├── README.md                          # This file
├── run.sh                             # Main scenario executor
├── data/
│   ├── sensors.csv                    # Valid sensor telemetry (35 rows, ~2KB)
│   ├── sensors_malformed.csv          # Intentionally broken data
│   ├── incident.json                  # Incident metadata
│   ├── sensor_config.yaml             # Sensor configurations
│   ├── runbook.md                     # Incident response procedures
│   └── validation_checklist.txt      # Phase 1 checklist
├── scripts/
│   ├── validate_phase1.sh             # Phase 1 output validation
│   ├── validate_phase2.sh             # Phase 2 output validation
│   └── validate_phase3.sh             # Phase 3 output validation
└── out/                               # Generated during execution
    ├── phase1_validation.json         # Phase 1 output
    ├── phase2_analysis.md             # Phase 2 output
    ├── incident_report.md             # Phase 3 output
    ├── errors.log                     # Error recovery log
    ├── sensor-001-stats.json          # Child thread output
    └── sensor-003-stats.json          # Child thread output
```

## License

This scenario is part of the marian-agent evaluation suite and follows the same license as the parent repository.

## References

- [Braintrust: Agent Evaluation](https://www.braintrust.dev/articles/agent-evaluation) - Multi-step agent testing framework
- [Acceldata: Agentic AI for DataOps](https://www.acceldata.io/blog/agentic-ai-for-dataops-from-alert-fatigue-to-fully-automated-incident-remediation) - Real-world incident remediation
- [Medium: Multi-Step Evaluation Gaps](https://medium.com/@avigoldfinger/multi-step-evaluation-gaps-are-holding-back-ai-agents-lets-fix-them-together-4b25b101528f) - Evaluation challenges
- [Statsig: Agent Eval Frameworks](https://www.statsig.com/perspectives/evaluatingmultistepreasoningagenteval) - Practical evaluation patterns
- [Medium: Data Pipelines for Agentic AI](https://shivharibaral.medium.com/how-to-build-a-real-world-data-pipeline-for-agentic-ai-71c8fd46aadd) - Data architecture patterns
