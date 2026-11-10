"""Produce one disposable catalog and its oracle through the supplied Amethyst implementation."""
import base64
from dataclasses import asdict
import json
import os
from pathlib import Path
import struct
import sys

from build_amethyst_snapshot_fixture import FixtureGame


def portable(value):
    if isinstance(value, bytes):
        return list(value)
    if isinstance(value, dict):
        return {key: portable(item) for key, item in value.items()}
    if isinstance(value, (list, tuple)):
        return [portable(item) for item in value]
    return value


def produce(root, extension, source):
    os.environ['AMETHYST_FILEGRAPH_EXTENSION'] = str(extension)
    sys.path.insert(0, str(source / 'src'))
    from Utils.filegraph.service import LibrarySession
    from Utils.filegraph.native import API_VERSION, SCHEMA_VERSION, ENGINE_REVISION, RULES_REVISION
    import msgpack
    contract = dict(api=API_VERSION, schema=SCHEMA_VERSION, engine=ENGINE_REVISION, rules=RULES_REVISION)
    FixtureGame.filemap_casing = 'preserve' if SCHEMA_VERSION >= 10 else 'upper'
    FixtureGame.filemap_exclude_dirs = ('excluded',)
    FixtureGame.conflict_detection_ignore_filenames = {'ignored.nif'}
    FixtureGame.conflict_detection_ignore_foldernames = {'ignored'}
    profile = root / 'profiles/default'
    profile.mkdir(parents=True)
    (profile / 'modlist.txt').write_text('+RootTop\n+Beta\n+Alpha\n-Gamma\n')
    (profile / 'loadorder.txt').write_text('Fixture.esp\nDormant.esp\n')
    (profile / 'plugins.txt').write_text('*Fixture.esp\nDormant.esp\n')
    (profile / 'profile_state.json').write_text(json.dumps({'mod_strip_prefixes': {'RootTop': ['Wrapper']}}))
    header = b'HEDR' + struct.pack('<HfII', 12, 1.7, 0, 0x800)
    plugin = struct.pack('<4sIIIII', b'TES4', len(header), 0, 0, 0, 44) + header
    files = {'mods/Alpha/Fixture.esp': plugin, 'mods/Beta/Fixture.esp': plugin,
             'mods/Gamma/Dormant.esp': plugin, 'mods/Gamma/Meshes/dormant.nif': b'disabled',
             'mods/Alpha/Meshes/example.nif': b'Alpha', 'mods/Beta/Meshes/example.nif': b'Beta',
             'mods/Alpha/Meshes/overwrite.nif': b'Alpha', 'overwrite/Meshes/overwrite.nif': b'overwrite',
             'Root_Folder/Data/Meshes/root.nif': b'root',
             'mods/Alpha/meshes/CaseVariant.nif': b'Alpha', 'mods/Beta/MESHES/casevariant.NIF': b'Beta',
             'mods/Alpha/Excluded/hidden.nif': b'excluded',
             'mods/RootTop/meta.ini': b'[General]\nrootFolder=true\n',
             'mods/RootTop/Wrapper/Data/Meshes/top.nif': b'root-top'}
    for mod in ['Alpha', 'Beta']:
        files[f'mods/{mod}/Meshes/ignored.nif'] = mod.encode()
        files[f'mods/{mod}/Meshes/ignored/folder.nif'] = mod.encode()
        files[f'mods/{mod}/Fixture.bsa'] = (Path(__file__).resolve().parents[2] / 'src/housecarl-mcp-tests/fixtures/skypatcher/IniFixture.bsa').read_bytes()
    if RULES_REVISION >= 19:
        (profile / 'modlist.txt').write_text('+RootTop\n+Beta\n+Alpha\n+beta\n-Gamma\n')
        files['mods/beta/Fixture.esp'] = plugin
        files['mods/beta/Fixture.bsa'] = files['mods/Beta/Fixture.bsa']
        files['mods/beta/Meshes/example.nif'] = b'case-distinct owner'
    for relative, data in files.items():
        path = root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)
    (root / 'game/Data').mkdir(parents=True)
    library = LibrarySession(FixtureGame(root), root)
    library.refresh(profile)
    session = library.open_profile(profile)
    session.reconcile(operation_hint={'kind': 'full'})
    snapshot = session.snapshot()
    winners = [portable(asdict(winner)) for winner in snapshot.export().winners]
    providers = [{'namespace': winner.namespace, 'target': winner.target,
                  'destination_key': list(winner.destination_key),
                  'providers': [asdict(p) for p in snapshot.providers(winner.target, winner.destination_key, namespace=winner.namespace)]}
                 for winner in snapshot.export().winners]
    # Compare the export path with independently paged winners, including a non-default page size.
    paged = []
    cursor = 0
    while True:
        page = list(snapshot.iter_winners(after_id=cursor, limit=2))
        if not page:
            break
        paged.extend(portable(asdict(w)) for w in page)
        cursor = page[-1].candidate_id
    status = asdict(library.status())
    if {key: status[key + '_version' if key in ('api', 'schema') else key + '_revision'] for key in contract} != contract:
        raise ValueError('producer Python and native component contracts differ')
    expected = {'contract': contract, 'status': status, 'generation': snapshot.generation,
                'inventory_generation': snapshot.inventory_generation, 'winners': winners,
                'providers': providers, 'export_matches_paging': sorted(winners, key=lambda w: w['candidate_id']) == sorted(paged, key=lambda w: w['candidate_id']),
                'deployment_unchanged': session.deployment_unchanged(snapshot.generation, 'hardlink'),
                'files': {name: base64.b64encode(data).decode() for name, data in files.items()}}
    if RULES_REVISION >= 18:
        expected['conflict_only'] = [portable(asdict(f)) for f in snapshot.mod_files('Beta')
                                     if 'ignored' in str(f)]
    config = root / 'manager-config'
    config.mkdir()
    (config / 'paths.json').write_text(json.dumps({'staging_path': str(root), 'game_path': str(root / 'game')}))
    (config / 'deploy_state.json').write_text(json.dumps({'last_active_profile': 'default', 'deploy_active': False}))
    (root / 'connection.json').write_text(json.dumps({'schemaVersion': 1, 'manager': 'amethyst', 'gameId': 'skyrim_se',
        'profileRoot': str(root), 'gameConfigDir': str(config), 'pathsFile': str(config / 'paths.json'), 'deployStateFile': str(config / 'deploy_state.json')}))
    return expected


if __name__ == '__main__':
    root, extension, source, output = map(Path, sys.argv[1:])
    output.write_text(json.dumps(produce(root, extension, source), indent=2) + '\n')
