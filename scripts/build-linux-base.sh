#!/usr/bin/env bash
# Regenerate the complete rulebook and publish a self-contained Linux integration build.
set -euo pipefail

repo="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet="${DOTNET:-dotnet}"
out="$repo/dist/linux-base"

"$dotnet" clean "$repo/housecarl.sln" -c Release -m:1 -v quiet
"$dotnet" restore "$repo/src/housecarl-generator" --disable-parallel -m:1
"$dotnet" build "$repo/src/housecarl-generator" -c Release --no-restore -m:1
mkdir -p -- "$out/generated" "$out/references" "$out/server"
"$dotnet" "$repo/src/housecarl-generator/bin/Release/net9.0/housecarl-generator.dll" \
  "$out/generated" "$out/references"
"$dotnet" restore "$repo/src/housecarl-mcp" -r linux-x64 --disable-parallel -m:1
"$dotnet" publish "$repo/src/housecarl-mcp" -c Release -r linux-x64 \
  --self-contained true --no-restore -p:PublishTrimmed=false -m:1 -o "$out/server"
rm -f -- "$out/server"/appsettings*.json
cp -- "$out/generated/corpus.json" "$out/server/corpus.json"

test -x "$out/server/housecarl-mcp"
test -f "$out/server/libhostfxr.so"
test -s "$out/server/corpus.json"
echo "Linux integration build: $out/server"
