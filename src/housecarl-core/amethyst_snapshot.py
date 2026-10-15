"""Read Amethyst's own snapshot from a disposable SQLite backup, never the live library.

The native component restores the saved intent using its own resolver. Its catalog
initialization and projection-cache writes are confined to the supplied private copy.
Only Python's standard library is needed; winner pages remain native MessagePack.
"""
import importlib.util
import json
from pathlib import Path
import struct
import sys

NATIVE_API = 13
NATIVE_SCHEMA = 9


def frame(payload):
    """Send one length-prefixed payload without mixing logging into stdout."""
    sys.stdout.buffer.write(struct.pack('<I', len(payload)))
    sys.stdout.buffer.write(payload)
    sys.stdout.buffer.flush()


def read_snapshot(copy_root, profile_id, extension, deploy_mode):
    """Open only the caller's copy and serve complete, cursor-paged native winners."""
    if sys.version_info < (3, 12):
        raise RuntimeError('Amethyst snapshot reads require Python 3.12 or newer')
    root = Path(copy_root)
    if not root.name.startswith('hc-amethyst-capture-') or not (root / 'filegraph.sqlite3').is_file():
        raise RuntimeError('Amethyst snapshot helper requires a private catalog copy')
    spec = importlib.util.spec_from_file_location('amethyst_filegraph', extension)
    if spec is None or spec.loader is None:
        raise RuntimeError('cannot load the packaged Amethyst filegraph component')
    native = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(native)
    if native.api_version() != NATIVE_API or native.schema_version() != NATIVE_SCHEMA:
        raise RuntimeError('unsupported Amethyst native component; this preview requires API 13 and schema 9')
    library = native.LibrarySession.open(root)
    profile = library.open_profile(profile_id)
    snapshot = profile.snapshot()
    frame(json.dumps({'api': native.api_version(), 'schema': native.schema_version(),
                      'generation': snapshot.generation,
                      'inventory_generation': snapshot.inventory_generation}).encode('utf-8'))
    for cursor in sys.stdin:
        request = cursor.strip()
        if request == 'deployment':
            frame(json.dumps(profile.deployment_unchanged(
                snapshot.generation, deploy_mode)).encode('utf-8'))
            break
        frame(snapshot.iter_winners(after_id=int(request), limit=1000))


if __name__ == '__main__':
    try:
        read_snapshot(*sys.argv[1:])
    except Exception as exc:
        print(str(exc), file=sys.stderr)
        raise SystemExit(1)
