"""Read native provider chains in a disposable catalog for the external experiment."""
import importlib.util
import json
from pathlib import Path
import sys

import msgpack


if __name__ == '__main__':
    extension, root, profile, oracle = sys.argv[1:]
    spec = importlib.util.spec_from_file_location('amethyst_filegraph', extension)
    native = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(native)
    library = native.LibrarySession.open(Path(root))
    snapshot = library.open_profile(profile).snapshot()
    rows = []
    for winner in json.loads(Path(oracle).read_text())['winners']:
        rows.append({'namespace': winner['namespace'], 'target': winner['target'], 'destination_key': winner['destination_key'],
                     'providers': msgpack.unpackb(snapshot.providers(winner['namespace'], winner['target'], bytes(winner['destination_key'])), raw=False)})
    print(json.dumps(rows))
