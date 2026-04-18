#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/../.." && pwd)"

cd "$repo_root"
dotnet build Agent.slnx -c Release

cd "$repo_root/samples/Acp.EverythingMcpDemo"

# Create a new session for this cwd (includes mcpServers from .acpxrc.json)
npx -y acpx@latest --cwd . --approve-all --non-interactive-permissions fail sessions new --name demo

# One-shot prompt
# NOTE: The harness enforces: report_intent must be called before other tools.
npx -y acpx@latest --cwd . --approve-all --non-interactive-permissions fail prompt --session demo \
  'You MUST follow these rules exactly:
1) You MUST call tool report_intent first.
2) You MUST NOT call any tool with missing required fields.
3) For execute_command: the arguments object MUST be exactly {"command":"uname","args":["-a"]}.
4) You MUST call ALL of the following tools EXACTLY ONCE and IN THIS ORDER:
   report_intent → write_text_file → read_text_file → execute_command → everything__echo
   You are NOT allowed to skip any step.
5) Between tool calls, you MUST output tool calls only (no natural language).
6) Do NOT retry failed tools. If any tool fails, stop and output EXACTLY: FAILED.
7) ONLY AFTER everything__echo has completed successfully, you MUST output exactly two sentences of summary, then output EXACTLY: DONE.

Now do the work:
Call tool report_intent with arguments: {"intent":"mcp demo"}.
Call tool write_text_file with arguments: {"path":"demo.txt","content":"hello"}.
Call tool read_text_file with arguments: {"path":"demo.txt"}.
Call tool execute_command with arguments: {"command":"uname","args":["-a"]}.
Call tool everything__echo with arguments: {"message":"hello from mcp"}.
Then (and only then) summarize in 2 sentences.
Then output EXACTLY: DONE'
