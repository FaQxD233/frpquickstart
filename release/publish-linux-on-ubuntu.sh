#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
OUT="$ROOT/artifacts/linux-x64-self-contained"

publish_project() {
  local project="$1"
  local output="$2"

  dotnet publish "$ROOT/src/FrpQuickStart.$project/FrpQuickStart.$project.csproj" \
    -c Release \
    -r linux-x64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:EnableCompressionInSingleFile=true \
    -p:DebugType=None \
    -p:DebugSymbols=false \
    -o "$OUT/$output"

  find "$OUT/$output" -maxdepth 1 -name '*.pdb' -type f -delete
}

publish_project Server server
publish_project Client client

echo "Linux server published to $OUT/server"
echo "Linux client published to $OUT/client"
echo "Both binaries are self-contained and embed the required frp component."
