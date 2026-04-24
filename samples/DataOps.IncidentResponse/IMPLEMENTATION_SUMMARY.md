# DataOps Incident Response Scenario - Implementation Summary

## Task Completion Report

### Overview
Created a comprehensive, multi-turn, real-world agent evaluation scenario under `samples/DataOps.IncidentResponse/` that simulates a DataOps incident response workflow for manual log review evaluation.

### Research Phase

**Web Search Results (Tavily):**

1. **Agent Evaluation Patterns** ([Braintrust](https://www.braintrust.dev/articles/agent-evaluation))
   - Multi-step agent evaluation tests decision quality across tool calls
   - Requires systematic testing of reasoning, actions, and outputs
   - Single-turn LLM evals insufficient for agentic workflows

2. **DataOps Incident Response** ([Acceldata](https://www.acceldata.io/blog/agentic-ai-for-dataops-from-alert-fatigue-to-fully-automated-incident-remediation))
   - Real-world scenarios: alert fatigue → automated remediation
   - Structured runbooks for incident resolution
   - Data pipeline validation and recovery patterns

3. **Multi-Step Evaluation Challenges** ([Medium](https://medium.com/@avigoldfinger/multi-step-evaluation-gaps-are-holding-back-ai-agents-lets-fix-them-together-4b25b101528f))
   - Multi-tool, multi-turn agents can finish in many "correct" ways
   - Evaluation requires clear success criteria per phase
   - Failure recovery is critical capability

4. **Data Pipelines for Agentic AI** ([Medium](https://shivharibaral.medium.com/how-to-build-a-real-world-data-pipeline-for-agentic-ai-71c8fd46aadd))
   - Schema contracts, audit logs, reproducible context
   - Clean data foundations enable reliable agent behavior
   - Observability crucial for production agents

5. **Agent Evaluation Frameworks** ([Statsig](https://www.statsig.com/perspectives/evaluatingmultistepreasoningagenteval))
   - Offline and online evaluation patterns
   - Tool-use checks and planning validation
   - Scoring rubrics adapted from product evaluation

### Scenario Design

**Narrative:** Critical temperature spike on production environmental sensors requiring structured incident response across 3 phases.

**Key Requirements Met:**

✅ **Multi-turn workflow:** 3 distinct phases with ~8-15 tool calls each  
✅ **Thread coordination:** Phase 2 creates 2 child threads (single mode) for parallel analysis  
✅ **Tool coverage:**
- Threading: `thread_start` (single + multi modes), `thread_send`, `thread_read`, `thread_list`, `thread_config`
- Host FS: `read_text_file`, `write_text_file`
- Host Exec: `execute_command` (optional for data processing)
- MCP: `everything__echo` (validation checkpoints)
- Intent: `report_intent` (phase tracking)

✅ **Intentional failures:** `sensors_malformed.csv` with invalid data for graceful recovery testing  
✅ **Compaction trigger:** ~2KB sensor CSV + JSON outputs may trigger compaction without overwhelming  
✅ **Tool budget:** Per-phase limits (15-20 calls) force efficient execution  
✅ **Explicit intent tracking:** `report_intent` required at phase boundaries  

### Dataset (Synthetic)

**Valid Data:**
- `sensors.csv`: 35 sensor readings (5 sensors × 7 timestamps) with critical/alert/warning/ok statuses
- `incident.json`: Incident metadata identifying affected sensors
- `sensor_config.yaml`: Sensor thresholds and alert routing
- `runbook.md`: Detailed incident response procedures
- `validation_checklist.txt`: Phase 1 checklist template

**Malformed Data (Intentional):**
- `sensors_malformed.csv`: Invalid numeric values, malformed lines, missing fields, empty IDs
- Tests error detection, logging, and graceful continuation

### Validation Scripts

**Phase 1:** `scripts/validate_phase1.sh`
- Checks: JSON schema, critical sensor identification, status count accuracy
- Uses Python for structured validation
- Exit code 0 on success, 1 on failure

**Phase 2:** `scripts/validate_phase2.sh`
- Checks: Markdown sections, statistics presence, trend analysis, word count >100
- Validates both sensor analyses present

**Phase 3:** `scripts/validate_phase3.sh`
- Checks: Report structure, timeline, tool call documentation, executive summary
- Validates word count >200 for comprehensive report

All scripts follow bash strict mode (`set -euo pipefail`) and repo conventions.

### Scenario Execution

**Main script:** `run.sh`
- Manages session lifecycle
- Executes 3 phases sequentially
- Extracts thread IDs from events for coordination
- Includes timeout handling and recovery patterns
- Validates all outputs before declaring success
- Follows Threading.Scenarios conventions

**Configuration:** `.acpxrc.json`
- Points to Agent.Server Release build
- Configures MCP Everything server via npx

### Phase Details

**Phase 1: Data Validation (8-10 tool calls)**
- Load incident metadata
- Parse valid sensor data
- **Intentional failure:** Parse malformed CSV
- **Recovery:** Log error, report intent, continue
- Generate `out/phase1_validation.json`

**Phase 2: Multi-Thread Analysis (12-15 tool calls)**
- Create 2 child threads with capability restrictions
- Each analyzes one affected sensor (parallel)
- Parent reads both children via `thread_read`
- Cross-reference with config thresholds
- Generate `out/phase2_analysis.md`

**Phase 3: Incident Report (8-10 tool calls)**
- Aggregate Phase 1 & 2 findings
- Document tool call history
- Generate executive summary
- Create `out/incident_report.md`
- MCP checkpoint (`everything__echo`)

### Documentation

**README.md:** 14KB comprehensive guide covering:
- Scenario overview and narrative
- Real-world pattern citations (5 research sources)
- Detailed phase descriptions with success criteria
- Tool coverage matrix
- Execution instructions (script + manual acpx)
- Evaluation guide (where to look in logs, what to check)
- Scoring rubric (100 points across 4 categories)
- Common failure modes
- Extension ideas
- Complete file tree

### Repository Integration

**Consistency with existing samples:**
- Follows Threading.Scenarios bash patterns
- Uses same timeout environment variables (`ACP_TIMEOUT`, `ACPX_TTL`, etc.)
- Includes recovery helpers (extracting IDs from events)
- Supports `--prompt-retries` for transient failures
- Uses strict mode, Python helpers, JSON parsing patterns

**Testing:**
- All existing tests pass: `dotnet test Agent.slnx -c Release -- -m:1`
- 470 total tests across 3 projects
- No regressions introduced

**Git commit:**
```
feat(samples): add DataOps.IncidentResponse multi-turn evaluation scenario
```
Conventional commit with detailed body explaining features, dataset, validation, and research basis.

### Deliverables Summary

✅ **samples/DataOps.IncidentResponse/README.md** - Comprehensive documentation  
✅ **samples/DataOps.IncidentResponse/run.sh** - Main scenario executor  
✅ **samples/DataOps.IncidentResponse/data/** - Synthetic dataset (8 files)  
✅ **samples/DataOps.IncidentResponse/scripts/** - Validation scripts (3 phases)  
✅ **samples/DataOps.IncidentResponse/.acpxrc.json** - MCP configuration  
✅ **samples/DataOps.IncidentResponse/out/** - Output directory with .gitignore  

**Total:** 14 files, 1166 lines added

### Key Innovations

1. **Real-world inspiration:** Based on actual DataOps incident response patterns, not synthetic abstract tasks
2. **Intentional failure design:** Malformed data tests recovery without being catastrophic
3. **Multi-level validation:** Automated scripts check invariants, but scenario designed for manual log review
4. **Tool precision requirements:** Forces correct `mode`, `capabilities`, and parameter usage
5. **Narrative coherence:** Sensor incident story flows naturally across phases
6. **Scalability hooks:** Easy to extend (Phase 4, larger datasets, stricter SLAs)

### Usage Example

```bash
cd /home/node/.openclaw/workspace/marian-agent
bash samples/DataOps.IncidentResponse/run.sh \
  "dotnet src/Agent.Server/bin/Release/net8.0/Agent.Server.dll"
```

Outputs:
- Session logs: `.agent/sessions/<id>/threads/*/events.jsonl`
- Scenario log: `samples/DataOps.IncidentResponse/out/scenario.log`
- Validation JSONs, Markdown reports, error logs

### Evaluation Metrics

**Automated validation:** Pass/fail per phase via scripts  
**Manual review focus:**
- Turn-by-turn tool sequence
- Thread coordination correctness
- Error recovery behavior
- Compaction handling (re-reading vs assuming in-context)
- Budget adherence
- Output quality (substantive vs stub responses)

**Scoring rubric:** 100 points across 4 dimensions:
- Execution Quality (40 pts)
- Tool Precision (30 pts)
- Error Recovery (15 pts)
- Output Quality (15 pts)

### Next Steps

**For evaluators:**
1. Run scenario via `run.sh`
2. Review session events in `.agent/sessions/<id>/`
3. Check automated validation results
4. Score manually using provided rubric
5. Compare different models/prompt strategies

**For extension:**
- Add Phase 4 with `thread_send` enqueue operations
- Expand dataset to 500+ rows for compaction stress test
- Introduce time-based SLA constraints
- Add multi-mode interactive debugging phase

---

**Status:** ✅ Complete and committed  
**Commit:** `d28d496` on branch `main`  
**Tests:** All green (470 tests passed)
