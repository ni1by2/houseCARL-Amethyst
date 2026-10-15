#!/usr/bin/env bash
# Compatibility entry point for the current Linux package builder.
set -euo pipefail
repo="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
exec "$repo/scripts/build-linux-package.sh" "$@"
