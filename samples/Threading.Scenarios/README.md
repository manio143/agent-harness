# Threading Scenarios (manual E2E via acpx + Ollama)

These scripts exercise the harness threading engine end-to-end (ACP stdio server + `acpx`).

## Prereqs

- `acpx` installed on PATH
- Ollama OpenAI-compatible endpoint reachable (see `src/Agent.Server/appsettings.json`)

## Run all

```bash
cd /home/node/.openclaw/workspace/marian-agent
bash samples/Threading.Scenarios/run_all.sh
```

Artifacts:
- Output logs: `samples/Threading.Scenarios/out/*.log`
- Session data: `src/Agent.Server/.agent/sessions` (per `appsettings.json`)

## Scenarios

Note: child thread startup tasks are delivered via `NewThreadTask` and rendered in the prompt as:

- `<thread_created id="..." parent_id="..." />`
- optional fork notice (`<notice>...`) when created with `context=fork`
- `<task>...` with the startup message


1. **scenario1_child_immediate_persisted**
   - create child thread (immediate)
   - second turn reads child thread and quotes child output

2. **scenario2_enqueue_idle_ordering**
   - create child (immediate)
   - enqueue follow-up to child
   - verify main receives idle notification and causal ordering holds (by inspection/logs)

3. **scenario3_self_send_enqueue_no_deadlock**
   - `thread_send` to `main` with `delivery=enqueued`
   - verify no hang and next turn still executes tools (catalog stability)
