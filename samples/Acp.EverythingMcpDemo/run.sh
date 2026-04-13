#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/../.." && pwd)"

cd "$repo_root"
dotnet build Agent.slnx -c Release

cd "$repo_root/samples/Acp.EverythingMcpDemo"

# Create a new session for this cwd (includes mcpServers from .acpxrc.json)
npx -y acpx@latest --cwd . sessions new --name demo

# One-shot prompt
npx -y acpx@latest --cwd . prompt --session demo \
  "Create /tmp/demo.txt with 'hello'. Read it back. Run 'uname -a'. Then call an MCP tool from the everything server and summarize." 
