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
Then create /tmp/demo.txt with "hello". Read it back. Run "uname -a".
Then call an MCP tool from the everything server and summarize.'
