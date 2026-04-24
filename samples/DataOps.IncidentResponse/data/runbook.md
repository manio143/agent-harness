# DataOps Incident Response Runbook

## Incident: INC-2026-04-24-001

**Severity:** Critical  
**Reported:** 2026-04-24T00:35:00Z  
**SLA Deadline:** 2026-04-24T02:35:00Z (2 hours)

## Affected Systems
- Sensor Network (Production)
- Data Pipeline (Ingestion Layer)
- Alert Aggregation Service

## Symptoms
- Temperature spike on sensors: sensor-001, sensor-003
- Alert threshold breached (>25°C)
- Critical threshold breached (>30°C)

## Response Procedures

### Phase 1: Data Validation (Priority: High)
**Owner:** Data Engineering Team  
**Duration:** 15 minutes

1. **Load incident metadata** from `data/incident.json`
2. **Parse sensor telemetry** from `data/sensors.csv`
3. **Validate data integrity**:
   - Check for missing timestamps
   - Verify sensor IDs match affected list
   - Confirm status field values (ok/warning/alert/critical)
4. **Identify anomalies**:
   - Temperature readings >25°C (warning)
   - Temperature readings >30°C (critical)
   - Pressure outside normal range (1012-1014 hPa)

**Expected Output:** Validation report in `out/phase1_validation.json`

**Note:** The malformed sensor readings in `sensors_malformed.csv` will fail parsing. Agent must detect and recover.

### Phase 2: Isolation & Analysis (Priority: High)
**Owner:** Site Reliability Team  
**Duration:** 20 minutes

1. **Create isolated analysis thread** for each affected sensor
2. **Calculate statistics** per sensor:
   - Min/Max/Average temperature
   - Trend direction (increasing/decreasing/stable)
   - Duration of anomaly (time above threshold)
3. **Cross-reference** with configuration in `sensor_config.yaml`
4. **Generate recommendations**:
   - Immediate actions (throttle/shutdown)
   - Investigation steps
   - Escalation criteria

**Expected Output:** Analysis results in `out/phase2_analysis.md`

### Phase 3: Incident Report (Priority: Medium)
**Owner:** Incident Commander  
**Duration:** 10 minutes

1. **Aggregate findings** from Phase 1 & 2
2. **List all tool calls executed** during investigation
3. **Compile incident timeline** with timestamps
4. **Generate executive summary**:
   - Root cause (if identified)
   - Impact assessment
   - Mitigation status
   - Follow-up actions

**Expected Output:** Final report in `out/incident_report.md`

## Success Criteria
- [ ] All sensor data validated
- [ ] Affected sensors identified and isolated
- [ ] Statistical analysis completed
- [ ] Incident report generated
- [ ] No data loss during processing
- [ ] All phases completed within SLA (2 hours)

## Escalation
If critical threshold persists >30 minutes OR >5 sensors affected:
- Page on-call engineering lead
- Engage product management
- Prepare customer communication

## Tools Required
- Thread management (thread_start, thread_send, thread_read, thread_list)
- File I/O (read_text_file, write_text_file)
- Command execution (execute_command for grep/wc/sort)
- MCP tools (everything__echo for validation checkpoints)
- Intent tracking (report_intent)
