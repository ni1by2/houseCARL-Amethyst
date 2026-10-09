"""Build a portable test catalog using Amethyst's actual Python service and native producer.

All files and configuration are synthetic and isolated. The JSON fixture retains the
producer's SQL, payload and winner/provider expectations; C# tests relocate it and
replace its tiny plugins with real Mutagen records. No installed profile is opened.
"""
import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import sqlite3
import struct
import subprocess
import sys
import tempfile

PIN = "2f54fddfa2015c3aa072ca2e56e2d0a4ca6173dd"


class FixtureGame:
    """The bounded Skyrim-like rules exercised by this synthetic producer fixture."""
    game_id = "skyrim_se"
    name = "fixture"
    archive_extensions = (".bsa",)
    plugin_extensions = (".esp", ".esm", ".esl")
    mod_install_extensions = (".nif", ".esp", ".esm", ".esl", ".ini", ".bsa")
    filemap_exclude_dirs = ()
    excluded_loose_filenames = ()
    filemap_casing = "upper"
    filemap_casing_pins = {}
    mod_folder_strip_prefixes = ()
    mod_folder_strip_prefixes_post = ()

    def __init__(self, root):
        self.root = root

    def get_effective_mod_staging_path(self):
        return self.root / "mods"

    def get_game_path(self):
        return self.root / "game"

    def get_mod_data_path(self):
        return self.root / "game" / "Data"


def build(source, extension, output):
    """Generate data and expectations independently of the C# implementation."""
    commit = subprocess.check_output(["git", "-C", str(source), "rev-parse", "HEAD"], text=True).strip()
    assert commit == PIN, commit
    assert not subprocess.check_output(["git", "-C", str(source), "diff", "HEAD", "--", "src", "native"], text=True)
    root = Path(tempfile.mkdtemp(prefix="hc-producer-fixture-"))
    os.environ["XDG_CONFIG_HOME"] = str(root / "config")
    os.environ["AMETHYST_FILEGRAPH_EXTENSION"] = str(extension)
    sys.path.insert(0, str(source / "src"))
    from Utils.filegraph.service import LibrarySession
    import msgpack
    profile = root / "profiles" / "default"
    profile.mkdir(parents=True)
    (profile / "modlist.txt").write_text("+Beta\n+Alpha\n-Gamma\n")
    (profile / "loadorder.txt").write_text("Fixture.esp\nDormant.esp\n")
    (profile / "plugins.txt").write_text("*Fixture.esp\nDormant.esp\n")
    # Minimal TES4 headers; the engine tests replace these with Mutagen-written plugins.
    header_body = b"HEDR" + struct.pack("<HfII", 12, 1.7, 0, 0x800)
    plugin = struct.pack("<4sIIIII", b"TES4", len(header_body), 0, 0, 0, 44) + header_body
    files = {
        "mods/Alpha/Fixture.esp": plugin,
        "mods/Beta/Fixture.esp": plugin,
        "mods/Gamma/Dormant.esp": plugin,
        "mods/Alpha/Meshes/example.nif": b"Alpha",
        "mods/Beta/Meshes/example.nif": b"Beta",
        "mods/Alpha/Meshes/overwrite.nif": b"Alpha",
        "overwrite/Meshes/overwrite.nif": b"overwrite",
        "Root_Folder/Data/Meshes/root.nif": b"root",
    }
    repo = Path(__file__).resolve().parents[2]
    files["mods/Alpha/Fixture.bsa"] = (repo / "src/housecarl-mcp-tests/fixtures/skypatcher/IniFixture.bsa").read_bytes()
    for relative, data in files.items():
        path = root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)
    (root / "game" / "Data").mkdir(parents=True)
    library = LibrarySession(FixtureGame(root), root)
    library.refresh(profile)
    session = library.open_profile(profile)
    session.reconcile(operation_hint={"kind": "full"})
    snapshot = session.snapshot()
    winners = []
    for winner in snapshot.export().winners:
        namespace = winner.namespace
        providers = snapshot.providers(winner.target, winner.destination_key, namespace=namespace)
        winners.append({"destination": winner.destination_key.decode(), "namespace": namespace,
                        "mod": winner.mod_name, "source": winner.source_rel.decode(),
                        "providers": [p.mod_name for p in providers]})
    with sqlite3.connect(root / "filegraph.sqlite3") as database:
        sql = "\n".join(database.iterdump()) + f"\nPRAGMA user_version={database.execute('PRAGMA user_version').fetchone()[0]};"
        intent = msgpack.unpackb(database.execute("SELECT intent_payload FROM profiles").fetchone()[0], raw=False)
    def json_value(value):
        if isinstance(value, bytes):
            return list(value)
        if isinstance(value, dict):
            return {key: json_value(item) for key, item in value.items()}
        if isinstance(value, list):
            return [json_value(item) for item in value]
        return value
    result = {"producer": commit, "extension_sha256": hashlib.sha256(extension.read_bytes()).hexdigest(),
              "root": str(root), "catalog_sql": sql, "files": {p: base64.b64encode(d).decode() for p, d in files.items()},
              "profile_files": {p.name: p.read_text() for p in profile.iterdir() if p.is_file()}, "winners": winners, "intent": json_value(intent)}
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, indent=2) + "\n")
    print(json.dumps({"producer": commit, "fixture": str(output), "root": str(root), "winners": winners}, indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--extension", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    build(args.source.resolve(), args.extension.resolve(), args.output.resolve())
