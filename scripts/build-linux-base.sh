#!/usr/bin/env bash
# Regenerate the complete rulebook and publish a self-contained Linux integration build.
set -euo pipefail

repo="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet="${DOTNET:-dotnet}"
out="$repo/dist/linux-base"
extension="${AMETHYST_FILEGRAPH_EXTENSION:?Set AMETHYST_FILEGRAPH_EXTENSION to the tested Amethyst API-13 native module}"
test -f "$extension"
extension_schema10="${AMETHYST_FILEGRAPH_EXTENSION_SCHEMA10:?Set AMETHYST_FILEGRAPH_EXTENSION_SCHEMA10 to the pinned schema-10 native module}"
test -f "$extension_schema10"
version="$(python3 - "$repo/plugin/.claude-plugin/plugin.json" <<'PY'
import json
import sys
with open(sys.argv[1], encoding="utf-8") as stream:
    print(json.load(stream)["version"])
PY
)"
for native_contract in 9 10; do
  native_input="$extension"
  if [[ "$native_contract" == 10 ]]; then native_input="$extension_schema10"; fi
  python3 -I - "$native_input" "$native_contract" <<'PY'
import importlib.util
import sys
if sys.version_info < (3, 12):
    raise SystemExit('Python 3.12+ is required for the Amethyst snapshot reader')
spec = importlib.util.spec_from_file_location('amethyst_filegraph', sys.argv[1])
native = importlib.util.module_from_spec(spec)
spec.loader.exec_module(native)
if native.api_version() != 13 or native.schema_version() != int(sys.argv[2]):
    raise SystemExit(f'Expected Amethyst native API 13 and schema {sys.argv[2]}')
PY
done

"$dotnet" clean "$repo/housecarl.sln" -c Release -m:1 -v quiet
"$dotnet" restore "$repo/src/housecarl-generator" --disable-parallel -m:1
"$dotnet" build "$repo/src/housecarl-generator" -c Release --no-restore -m:1
rm -rf -- "$out/server"
mkdir -p -- "$out/generated" "$out/references" "$out/server"
"$dotnet" "$repo/src/housecarl-generator/bin/Release/net9.0/housecarl-generator.dll" \
  "$out/generated" "$out/references"
"$dotnet" restore "$repo/src/housecarl-mcp" -r linux-x64 --disable-parallel -m:1
"$dotnet" publish "$repo/src/housecarl-mcp" -c Release -r linux-x64 \
  --self-contained true --no-restore -p:PublishTrimmed=false -p:Version="$version" -m:1 -o "$out/server"
rm -f -- "$out/server"/appsettings*.json
cp -- "$out/generated/corpus.json" "$out/server/corpus.json"
cp -- "$extension" "$out/server/amethyst_filegraph.abi3.so"
cp -- "$extension_schema10" "$out/server/amethyst_filegraph_schema10.abi3.so"
cp -- "$repo/src/housecarl-core/amethyst_snapshot.py" "$out/server/amethyst_snapshot.py"

# Keep the native filegraph contract and the Python adapter pinned together. The
# helper checks the loaded extension at runtime; this build-time check catches a
# copied helper from a stale source tree before an archive can be assembled.
python3 -I - "$out/server/amethyst_snapshot.py" <<'PY'
import ast
import pathlib
import sys

path = pathlib.Path(sys.argv[1])
tree = ast.parse(path.read_text(encoding="utf-8"), filename=str(path))
contracts = {
    node.targets[0].id: ast.literal_eval(node.value)
    for node in tree.body
    if isinstance(node, ast.Assign)
    and len(node.targets) == 1
    and isinstance(node.targets[0], ast.Name)
    and isinstance(node.value, (ast.Constant, ast.Tuple))
}
if contracts.get("NATIVE_API") != 13 or contracts.get("NATIVE_SCHEMAS") != (9, 10):
    raise SystemExit("snapshot helper contract mismatch: expected native API 13 / schemas 9 and 10")
if not any(isinstance(node, ast.FunctionDef) and node.name == "read_snapshot" for node in tree.body):
    raise SystemExit("snapshot helper is missing read_snapshot")
PY

test -x "$out/server/housecarl-mcp"
test -f "$out/server/libhostfxr.so"
test -s "$out/server/corpus.json"
test -s "$out/server/amethyst_snapshot.py"
echo "Linux integration build: $out/server"
