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
1) You may call tools.
2) You MUST call tool report_intent first.

Now do the work:
Call tool report_intent with arguments: {"intent":"mcp demo"}.
Then call tool write_text_file with arguments: {"path":"/tmp/demo.txt","content":"hello"}.
Then call tool read_text_file with arguments: {"path":"/tmp/demo.txt"}.
Then call tool execute_command with arguments: {"command":"uname -a"}.
Then call tool everything__echo with arguments: {"message":"hello from mcp"}.
Then briefly summarize what happened.'
