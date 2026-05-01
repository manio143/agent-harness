# Shell.CommandSuggestionsDemo

Demonstrates the PowerShell shell command:

- `Find-AgentCommand -Intent "..."`

This command returns a small set of suggested cmdlet/tool names for a natural-language intent.

## How to run

From the repo root:

```bash
bash samples/Shell.CommandSuggestionsDemo/run.sh
```

### Notes

- This sample disables automatic `report_intent` suggestions (to avoid extra work in the turn):
  - `AGENTSERVER_AgentServer__Core__IncludeSuggestionsInReportIntent=false`
- This sample enables shell suggestions:
  - `AGENTSERVER_AgentServer__Core__IncludeSuggestionsInShell=true`
