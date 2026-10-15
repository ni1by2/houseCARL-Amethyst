#!/usr/bin/env bash
set -euo pipefail

repo="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
if python3 - <<'PY'
import sys
raise SystemExit(0 if sys.version_info >= (3, 12) else 1)
PY
then :; else
  echo "Python 3.12 or newer is required to assemble the Amethyst Linux package." >&2
  exit 2
fi

if [[ -z "${AMETHYST_FILEGRAPH_EXTENSION:-}" ]]; then
  echo "Set AMETHYST_FILEGRAPH_EXTENSION to the tested Amethyst API-13 native module." >&2
  exit 2
fi

"$repo/scripts/build-linux-base.sh"
python3 "$repo/scripts/build-linux-tools.py"
python3 "$repo/scripts/package-linux.py"
