#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/../.." && pwd)"

cd "$repo_root"
dotnet build Agent.slnx -c Release

cd "$repo_root/samples/Acp.EverythingMcpDemo"

# Create a new session for this cwd (includes mcpServers from .acpxrc.json)
npx -y acpx@latest --cwd . sessions new --name demo

# One-shot prompt
# NOTE: The harness enforces: report_intent must be called before other tools.
npx -y acpx@latest --cwd . prompt --session demo \
  'You MUST follow these rules exactly:
1) You MUST call tool report_intent first.
2) You MUST NOT call any tool with missing required fields.
3) For execute_command: the arguments object MUST be exactly {"command":"uname -a"}.
4) Do NOT retry failed tools. If any tool fails, stop and output EXACTLY: FAILED.
5) After all tool calls succeed, output EXACTLY: DONE.

Now do the work (tool calls only; no extra commentary between them):
Call tool report_intent with arguments: {"intent":"mcp demo"}.
Call tool write_text_file with arguments: {"path":"demo.txt","content":"hello"}.
Call tool read_text_file with arguments: {"path":"demo.txt"}.
Call tool execute_command with arguments: {"command":"uname -a"}.
Call tool everything__echo with arguments: {"message":"hello from mcp"}.
Then summarize in 2 sentences.
Then output EXACTLY: DONE'
