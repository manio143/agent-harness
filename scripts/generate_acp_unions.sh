#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/.."

dotnet run -c Release --project tools/Agent.Acp.UnionGen/Agent.Acp.UnionGen.csproj
