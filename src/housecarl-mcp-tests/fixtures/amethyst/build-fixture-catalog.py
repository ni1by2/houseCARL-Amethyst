"""Save synthetic test inputs through Amethyst's native producer, without third-party Python packages."""
import importlib.util
from pathlib import Path
import sys

root, profile, payloads, extension = sys.argv[1:]
spec = importlib.util.spec_from_file_location('amethyst_filegraph', extension)
native = importlib.util.module_from_spec(spec)
spec.loader.exec_module(native)
library = native.LibrarySession.open(Path(root))
library.set_ready(False)
for payload in sorted(Path(payloads).glob('mod-*.msgpack')):
    library.replace_mod_manifest(payload.read_bytes())
library.set_ready(True)
library.open_profile(profile).reconcile((Path(payloads) / 'intent.msgpack').read_bytes())
