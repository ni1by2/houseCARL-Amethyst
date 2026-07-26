#!/usr/bin/env bash
# Build the complete, self-contained Linux x86_64 release from tracked sources.
set -euo pipefail

repo="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet="${DOTNET:-dotnet}"
plugin="$repo/plugin"
dist="$repo/dist"
bundle="$dist/housecarl-amethyst"
server="$bundle/housecarl/server"
skills="$bundle/housecarl/skills"
release="$repo/release"
manifest="$plugin/.claude-plugin/plugin.json"
version="$(sed -n 's/^[[:space:]]*"version":[[:space:]]*"\([^"]*\)".*/\1/p' "$manifest" | head -n 1)"

[[ -n "$version" ]] || { echo "error: version missing from $manifest" >&2; exit 1; }
echo "Building houseCARL-Amethyst $version"

# Rebuild the reflection corpus and its reference shards from the pinned Mutagen model.
rm -rf -- "$dist"
mkdir -p -- "$bundle" "$server" "$skills" "$release"
"$dotnet" restore "$repo/housecarl.sln" --disable-parallel --maxcpucount:1
"$dotnet" build "$repo/src/housecarl-generator" --configuration Release --no-restore --maxcpucount:1
"$dotnet" "$repo/src/housecarl-generator/bin/Release/net9.0/housecarl-generator.dll" \
  "$repo/generated" "$repo/.claude/skills/mutagen-reference/references"

# The server is self-contained and deliberately untrimmed because record coverage is reflection-driven.
"$dotnet" publish "$repo/src/housecarl-mcp" --configuration Release --runtime linux-x64 \
  --self-contained true -p:PublishTrimmed=false -p:Version="$version" --output "$server" \
  --disable-build-servers --maxcpucount:1
rm -f -- "$server"/appsettings*.json
cp -- "$repo/generated/corpus.json" "$server/corpus.json"

# Copy the thirteen reviewed skills; development-only evaluations never enter a release.
for source in "$repo"/.claude/skills/*; do
  [[ -d "$source" ]] || continue
  cp -a -- "$source" "$skills/"
done
find "$skills" -type d -name evals -prune -exec rm -rf -- {} +
find "$skills" -type f -name _CORPUS_STATUS.md -delete

# Keep the Claude plugin metadata beside the server and the Codex umbrella beside the plugin.
cp -a -- "$plugin/.claude-plugin" "$bundle/housecarl/"
cp -- "$plugin/.mcp.json" "$plugin/LICENSE" "$plugin/THIRD-PARTY-NOTICES.txt" \
  "$plugin/README.md" "$plugin/CHANGELOG.md" "$bundle/housecarl/"
cp -a -- "$plugin/codex" "$bundle/"

# Publish the small installer as a separate self-contained executable.
"$dotnet" publish "$repo/src/housecarl-setup" --configuration Release --runtime linux-x64 \
  --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=true \
  -p:DebugType=None -p:DebugSymbols=false --output "$bundle" \
  --disable-build-servers --maxcpucount:1

# Stamp the human entry point without changing its tracked template.
sed "s/{{VERSION}}/$version/g" "$repo/packaging/START-HERE.txt" > "$bundle/START-HERE.txt"

# Refuse known build leaks before signing the extracted payload.
if find "$bundle" \( -name 'appsettings*.json' -o -name '_CORPUS_STATUS.md' -o -name evals \) -print -quit |
   grep -q .; then
  echo "error: development-only files entered the release" >&2
  exit 1
fi
if rg -l '/home/[^/]+/|[A-Z]:\\\\Users\\\\' "$bundle" \
     --glob '*.json' --glob '*.jsonl' --glob '*.md' --glob '*.txt' --glob '*.yml' --glob '*.yaml' |
   grep -q .; then
  echo "error: a developer path entered the release" >&2
  exit 1
fi

# Hash every installable file. The installer verifies this list before changing user state.
(
  cd -- "$bundle"
  find . -type f ! -name SHA256SUMS -print0 |
    sort -z |
    xargs -0 sha256sum |
    sed 's#  \./#  #'
) > "$bundle/SHA256SUMS"

# A stable tar order and metadata make identical source/tool inputs reproducible.
archive="$release/housecarl-amethyst-$version-linux-x64.tar.gz"
tar --sort=name --mtime='@0' --owner=0 --group=0 --numeric-owner \
  -C "$dist" -czf "$archive" housecarl-amethyst
sha256sum "$archive" > "$archive.sha256"

echo "Release: $archive"
echo "Checksum: $archive.sha256"
