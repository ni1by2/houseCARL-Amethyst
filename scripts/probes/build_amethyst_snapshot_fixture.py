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
import re
import sqlite3
import struct
import subprocess
import sys
import tempfile

PIN = "e98cf70fe63f6b4a64b39e780ee3ffe02e553c6e"
SCHEMA10_PIN = "abeae84afce940c6819f80c1cd4e6e51db2e935d"
RULES15_PIN = "7de35eaf0c7191defa7aee33d61452ddf73f7da3"
RULES18_PIN = "788a71262ca479b6cbccc53ff3a0083250e3833c"
RULES19_PIN = "8ac86bdb9bc6f32b5c49eae69890ae96e4f16943"


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


def dump_catalog(database):
    """Preserve embedded NULs in TEXT identities that SQLite's SQL quote() truncates."""
    def value(item):
        if item is None:
            return 'NULL'
        if isinstance(item, bytes):
            return "X'" + item.hex() + "'"
        if isinstance(item, str):
            return "CAST(X'" + item.encode().hex() + "' AS TEXT)" if '\0' in item else "'" + item.replace("'", "''") + "'"
        return repr(item)
    statements, emitted = [], set()
    tables = [row[0] for row in database.execute("SELECT name FROM sqlite_master WHERE type='table'")]
    for statement in database.iterdump():
        match = re.match(r'INSERT INTO "((?:[^"]|"")*)" VALUES', statement)
        if match:
            table = match[1].replace('""', '"')
            if table not in emitted:
                emitted.add(table)
                quoted = '"' + table.replace('"', '""') + '"'
                statements.extend(f'INSERT INTO {quoted} VALUES({",".join(map(value, row))});'
                                  for row in database.execute(f'SELECT * FROM {quoted}'))
        else:
            if statement.startswith('INSERT INTO '):
                raise ValueError('unrecognized SQLite insert in fixture dump')
            statements.append(statement)
    result = '\n'.join(statements)
    with sqlite3.connect(':memory:') as restored:
        restored.executescript(result)
        for table in tables:
            quoted = '"' + table.replace('"', '""') + '"'
            original = sorted(database.execute(f'SELECT * FROM {quoted}').fetchall(), key=repr)
            copied = sorted(restored.execute(f'SELECT * FROM {quoted}').fetchall(), key=repr)
            if original != copied:
                raise ValueError('fixture SQL round trip changed table ' + table)
    return result


def build(source, extension, output, schema=9, rules=None):
    """Generate data and expectations independently of the C# implementation."""
    commit = subprocess.check_output(["git", "-C", str(source), "rev-parse", "HEAD"], text=True).strip()
    rules = rules or (14 if schema == 10 else 13)
    assert (schema, rules) in ((9, 13), (10, 14), (10, 15), (10, 18), (10, 19))
    assert commit == (RULES19_PIN if rules == 19 else RULES18_PIN if rules == 18 else RULES15_PIN if rules == 15 else SCHEMA10_PIN if schema == 10 else PIN), commit
    assert not subprocess.check_output(["git", "-C", str(source), "diff", "HEAD", "--", "src", "native"], text=True)
    root = Path(tempfile.mkdtemp(prefix="hc-producer-fixture-"))
    for key, folder in {"HOME": "home", "XDG_CONFIG_HOME": "config", "XDG_STATE_HOME": "state",
                        "XDG_DATA_HOME": "data", "XDG_CACHE_HOME": "cache"}.items():
        path = root / folder
        path.mkdir()
        os.environ[key] = str(path)
    os.environ["AMETHYST_FILEGRAPH_EXTENSION"] = str(extension)
    sys.path.insert(0, str(source / "src"))
    from Utils.filegraph.service import LibrarySession
    import msgpack
    if schema == 10:
        FixtureGame.filemap_casing = "preserve"
    if rules >= 18:
        FixtureGame.filemap_exclude_dirs = ("excluded",)
        FixtureGame.conflict_detection_ignore_filenames = {"ignored.nif"}
        FixtureGame.conflict_detection_ignore_foldernames = {"ignored"}
    profile = root / "profiles" / "default"
    profile.mkdir(parents=True)
    (profile / "modlist.txt").write_text("+Beta\n+Alpha\n-Gamma\n")
    (profile / "loadorder.txt").write_text("Fixture.esp\nDormant.esp\n")
    (profile / "plugins.txt").write_text("*Fixture.esp\nDormant.esp\n")
    if rules >= 15:
        (profile / "modlist.txt").write_text("+RootTop\n+Beta\n+Alpha\n-Gamma\n")
        (profile / "profile_state.json").write_text(json.dumps({"mod_strip_prefixes": {"RootTop": ["Wrapper"]}}))
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
    if schema == 10:
        files.update({"mods/Alpha/meshes/CaseVariant.nif": b"Alpha",
                      "mods/Beta/MESHES/casevariant.NIF": b"Beta"})
    if rules >= 15:
        files.update({"mods/RootTop/meta.ini": b"[General]\nrootFolder=true\n",
                      "mods/RootTop/Wrapper/Data/Meshes/top.nif": b"top-level-root"})
    if rules >= 18:
        files["mods/Alpha/Excluded/hidden.nif"] = b"excluded"
        files["mods/Gamma/Meshes/dormant.nif"] = b"disabled"
        for mod in ("Alpha", "Beta"):
            files[f"mods/{mod}/Meshes/ignored.nif"] = mod.encode()
            files[f"mods/{mod}/Meshes/ignored/folder.nif"] = mod.encode()
    repo = Path(__file__).resolve().parents[2]
    files["mods/Alpha/Fixture.bsa"] = (repo / "src/housecarl-mcp-tests/fixtures/skypatcher/IniFixture.bsa").read_bytes()
    if rules == 19:
        (profile / "modlist.txt").write_text("+RootTop\n+Beta\n+Alpha\n+beta\n-Gamma\n")
        files["mods/beta/Meshes/example.nif"] = b"case-distinct owner"
        files["mods/beta/Meshes/lower-only.nif"] = b"lower owner only"
        files["mods/beta/Fixture.esp"] = plugin
        files["mods/beta/Fixture.bsa"] = files["mods/Alpha/Fixture.bsa"]
        files["mods/Beta/Fixture.bsa"] = files["mods/Alpha/Fixture.bsa"]
    for relative, data in files.items():
        path = root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)
    if rules >= 15:
        from Nexus.nexus_meta import read_meta
        assert read_meta(root / "mods/RootTop/meta.ini").root_folder, "fixture root metadata was not read"
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
        sql = dump_catalog(database) + f"\nPRAGMA user_version={database.execute('PRAGMA user_version').fetchone()[0]};"
        intent = msgpack.unpackb(database.execute("SELECT intent_payload FROM profiles").fetchone()[0], raw=False)
    def json_value(value):
        if isinstance(value, bytes):
            return list(value)
        if isinstance(value, dict):
            return {key: json_value(item) for key, item in value.items()}
        if isinstance(value, list):
            return [json_value(item) for item in value]
        return value
    assert library.status().schema_version == schema
    assert library.status().rules_revision == rules
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
    parser.add_argument("--schema", type=int, choices=(9, 10), default=9)
    parser.add_argument("--rules", type=int, choices=(13, 14, 15, 18, 19))
    args = parser.parse_args()
    build(args.source.resolve(), args.extension.resolve(), args.output.resolve(), args.schema, args.rules)
