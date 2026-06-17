#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
OUT="$ROOT/artifacts/linux-x64-self-contained/server"

dotnet publish "$ROOT/src/FrpQuickStart.Server/FrpQuickStart.Server.csproj" \
  -c Release \
  -r linux-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:EnableCompressionInSingleFile=true \
  -p:DebugType=None \
  -p:DebugSymbols=false \
  -o "$OUT"

find "$OUT" -maxdepth 1 -name '*.pdb' -type f -delete

echo "Linux server published to $OUT"
echo "frps is embedded in frpquick-server and will be extracted automatically on first run."
echo "The server binary is self-contained; .NET Runtime is not required on the target Ubuntu machine."
