"""Container-only compatibility experiment; this module does not approve runtime contracts."""
import ast
import hashlib
import json
import os
from pathlib import Path
import queue
import sqlite3
import struct
import subprocess
import sys
import tempfile
import threading


FIELDS = ('candidate_id', 'namespace', 'target', 'mod_key', 'destination_key', 'source_rel')


def canonical(rows):
    """Compare the winner fields consumed by the production adapter, preserving byte identities."""
    return sorted([{key: list(row[key]) if isinstance(row[key], bytes) else row[key] for key in FIELDS}
                   for row in rows], key=lambda row: row['candidate_id'])


def check_winners(expected, actual):
    if canonical(expected) != canonical(actual):
        raise ValueError('reader winner identities differ from the producer oracle')


def frame(stream):
    header = stream.read(4)
    if len(header) != 4:
        raise ValueError('native helper returned an incomplete frame header')
    size = struct.unpack('<I', header)[0]
    if not 0 < size <= 64 * 1024 * 1024:
        raise ValueError('native helper returned an invalid frame size')
    data = stream.read(size)
    if len(data) != size:
        raise ValueError('native helper returned an incomplete frame')
    return data


def fingerprint(root):
    return {str(path.relative_to(root)): hashlib.sha256(path.read_bytes()).hexdigest()
            for path in root.rglob('*') if path.is_file() and not path.name.endswith('-shm')
            and not (path.name.endswith('-wal') and path.stat().st_size == 0)}


def reader_metadata(extension):
    code = ("import importlib.util,json,sys; s=importlib.util.spec_from_file_location('amethyst_filegraph',sys.argv[1]); "
            "n=importlib.util.module_from_spec(s);s.loader.exec_module(n); "
            "print(json.dumps({'api':n.api_version(),'schema':n.schema_version()}))")
    return json.loads(subprocess.check_output([sys.executable, '-I', '-c', code, str(extension)], text=True, timeout=30))


def transport_contract(helper):
    """Read the baseline helper's declared coverage without extending its transport."""
    constants = {}
    for node in ast.parse(helper.read_text()).body:
        if isinstance(node, ast.Assign):
            for target in node.targets:
                if isinstance(target, ast.Name) and target.id in ('NATIVE_API', 'NATIVE_SCHEMAS'):
                    constants[target.id] = ast.literal_eval(node.value)
    if not isinstance(constants.get('NATIVE_API'), int) or not isinstance(constants.get('NATIVE_SCHEMAS'), (list, tuple)):
        raise ValueError('baseline helper does not declare recognizable API/schema transport coverage')
    return {'api': constants['NATIVE_API'], 'schemas': list(constants['NATIVE_SCHEMAS'])}


def compare_reader(root, expected, extension, helper):
    """Run the shipped Python transport unchanged against a backup, without its C# admission guard."""
    metadata = reader_metadata(extension)
    contract = expected['contract']
    if metadata != {key: contract[key] for key in ('api', 'schema')}:
        return {'status': 'not_applicable', 'reader': metadata, 'reason': 'reader API/schema differs from producer'}
    transport = transport_contract(helper)
    if contract['api'] != transport['api'] or contract['schema'] not in transport['schemas']:
        return {'status': 'unsupported_transport', 'reader': metadata, 'transport': transport,
                'reason': 'baseline helper cannot exercise this API/schema; native parity was not tested'}
    import msgpack
    with tempfile.TemporaryDirectory(prefix='hc-amethyst-capture-') as raw:
        backup = Path(raw)
        with sqlite3.connect(f'file:{root / "filegraph.sqlite3"}?mode=ro', uri=True) as original:
            with sqlite3.connect(backup / 'filegraph.sqlite3') as target:
                original.backup(target)
        with tempfile.TemporaryFile(mode='w+b') as errors:
            process = subprocess.Popen([sys.executable, '-I', str(helper), str(backup), str(root / 'profiles/default'),
                                        str(extension), 'hardlink', str(contract['schema'])],
                                       stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=errors)
            # Kill the entire helper on a blocked read as well as on a normal subprocess timeout.
            timer = threading.Timer(60, process.kill)
            timer.start()
            try:
                status = msgpack.unpackb(frame(process.stdout), raw=False)
                header = json.loads(frame(process.stdout))
                if (status['api_version'] != contract['api'] or status['schema_version'] != contract['schema']
                        or status['engine_revision'] != contract['engine'] or not status['ready']
                        or status['inventory_generation'] != expected['inventory_generation']):
                    raise ValueError('reader engine/readiness differs from producer')
                if header['generation'] != expected['generation'] or header['inventory_generation'] != expected['inventory_generation']:
                    raise ValueError('reader returned a different saved generation')
                rows = []
                cursor = 0
                while True:
                    process.stdin.write(f'{cursor}\n'.encode()); process.stdin.flush()
                    page = msgpack.unpackb(frame(process.stdout), raw=False)
                    if not isinstance(page, list):
                        raise ValueError('reader page is not an array')
                    if not page:
                        break
                    for row in page:
                        if row['candidate_id'] <= cursor:
                            raise ValueError('reader cursor failed to advance')
                        cursor = row['candidate_id']
                    rows.extend(page)
                process.stdin.write(b'deployment\n'); process.stdin.flush()
                deployment = json.loads(frame(process.stdout))
                process.stdin.close()
                if process.wait(timeout=10) != 0:
                    raise ValueError('native helper exited unsuccessfully')
                check_winners(expected['winners'], rows)
                if deployment != expected['deployment_unchanged']:
                    raise ValueError('reader deployment verification differs from producer')
                providers = json.loads(subprocess.check_output([sys.executable, str(Path(__file__).with_name('amethyst_compatibility_details.py')),
                    str(extension), str(backup), str(root / 'profiles/default'), '/output/producer-oracle.json'], text=True, timeout=60))
                if providers != expected['providers']:
                    raise ValueError('reader provider chains differ from producer')
                return {'status': 'pass', 'reader': {**metadata, 'engine': status['engine_revision'], 'rules': status['rules_revision']},
                        'winner_count': len(rows), 'generation': header['generation'], 'deployment_unchanged': deployment,
                        'provider_chains': len(providers),
                        'scope': 'native winner transport, provider chains and uncommitted-deployment verification; C# admission bypassed only by this external experiment'}
            except Exception as exc:
                if process.poll() is None:
                    process.kill()
                process.wait(timeout=10)
                errors.seek(0)
                raise ValueError(str(exc) + '; helper stderr: ' + errors.read().decode(errors='replace')[-2000:]) from exc
            finally:
                timer.cancel()
                if process.poll() is None:
                    process.kill(); process.wait(timeout=10)
                process.stdout.close()
                if not process.stdin.closed:
                    process.stdin.close()


class MCP:
    def __init__(self, server):
        self.errors = tempfile.TemporaryFile(mode='w+t')
        self.process = subprocess.Popen([str(server / 'housecarl-mcp')], cwd=server, stdin=subprocess.PIPE,
                                        stdout=subprocess.PIPE, stderr=self.errors, text=True)
        self.lines = queue.Queue()
        self.thread = threading.Thread(target=self.pump, daemon=True)
        self.thread.start()
        self.ident = 0

    def pump(self):
        for line in self.process.stdout:
            self.lines.put(line)
        self.lines.put(None)

    def send(self, message):
        self.process.stdin.write(json.dumps(message) + '\n'); self.process.stdin.flush()

    def rpc(self, method, params):
        self.ident += 1
        self.send({'jsonrpc': '2.0', 'id': self.ident, 'method': method, 'params': params})
        while True:
            line = self.lines.get(timeout=60)
            if line is None:
                self.errors.seek(0)
                raise ValueError('MCP closed stdout: ' + self.errors.read()[-4000:])
            message = json.loads(line)
            if message.get('id') == self.ident:
                if 'error' in message:
                    raise ValueError(str(message['error']))
                return message['result']

    def tool(self, name, arguments):
        return self.rpc('tools/call', {'name': name, 'arguments': arguments})

    def close(self):
        try:
            self.process.stdin.close()
            self.process.wait(timeout=10)
        except (BrokenPipeError, subprocess.TimeoutExpired):
            self.process.kill(); self.process.wait(timeout=10)
        self.thread.join(timeout=5)
        self.process.stdout.close(); self.errors.close()


def text(result):
    return '\n'.join(item.get('text', '') for item in result.get('content', []))


def server_probe(root, server):
    client = MCP(server)
    try:
        info = client.rpc('initialize', {'protocolVersion': '2025-06-18', 'capabilities': {},
                                        'clientInfo': {'name': 'amethyst-compatibility-suite', 'version': '1'}})
        client.send({'jsonrpc': '2.0', 'method': 'notifications/initialized'})
        selection = client.tool('housecarl_set_amethyst_connection',
                                {'manifest_path': str(root / 'connection.json'), 'filegraph_mode': 'saved-snapshot'})
        if selection.get('isError'):
            message = text(selection)
            if 'unsupported Amethyst filegraph schema or engine/rules revision' in message:
                return {'status': 'blocked_by_contract', 'version': info['serverInfo']['version'], 'diagnostic': message,
                        'write_checks': 'not_exercised'}
            raise ValueError('MCP connection failed for another reason: ' + message)
        status = client.tool('housecarl_load_order_status', {})
        if status.get('isError'):
            raise ValueError('MCP status failed: ' + text(status))
        assets = client.tool('housecarl_asset_status', {'asset_paths': ['Meshes/example.nif'], 'format': 'json'})
        if assets.get('isError') or 'Beta' not in text(assets) or 'unverified' not in text(assets):
            raise ValueError('MCP asset winner/provenance check failed: ' + text(assets))
        path = root / 'mods/Beta/Meshes/example.nif'
        original, stat = path.read_bytes(), path.stat()
        try:
            path.write_bytes(b'changed-after-refresh')
            stale = client.tool('housecarl_asset_status', {'asset_paths': ['Meshes/example.nif']})
            if not stale.get('isError') or 'changed' not in text(stale):
                raise ValueError('MCP accepted a changed saved source')
        finally:
            path.write_bytes(original); os.utime(path, ns=(stat.st_atime_ns, stat.st_mtime_ns))
        intent_path = root / 'profiles/default/loadorder.txt'
        original, stat = intent_path.read_bytes(), intent_path.stat()
        try:
            intent_path.write_bytes(original + b'\n')
            stale = client.tool('housecarl_load_order_status', {})
            if not stale.get('isError') or 'intent changed' not in text(stale):
                raise ValueError('MCP accepted changed saved profile intent')
        finally:
            intent_path.write_bytes(original); os.utime(intent_path, ns=(stat.st_atime_ns, stat.st_mtime_ns))
        return {'status': 'accepted', 'version': info['serverInfo']['version'],
                'checks': ['explicit_connection', 'status', 'asset_winner_and_provenance', 'changed_source_refusal', 'changed_profile_intent_refusal'],
                'write_checks': 'not_exercised'}
    finally:
        client.close()


def producer_invariants(expected):
    winners = expected['winners']
    find = lambda path: [row for row in winners if bytes(row['destination_key']).decode().lower() == path]
    if not expected['export_matches_paging']:
        raise ValueError('producer export differs from paged winners')
    for path, mod in [('data/meshes/example.nif', 'beta'), ('data/meshes/overwrite.nif', '[overwrite]'),
                      ('data/meshes/ignored.nif', 'beta'), ('data/meshes/ignored/folder.nif', 'beta')]:
        if not any(row['mod_key'] == mod for row in find(path)):
            raise ValueError('producer violated fixture winner invariant: ' + path)
    if any(row['mod_key'] == 'gamma' or b'excluded' in bytes(row['destination_key']).lower() for row in winners):
        raise ValueError('producer exposed disabled or excluded files')
    if not any(row['namespace'] == 'archive' for row in winners):
        raise ValueError('producer fixture failed to exercise archive winners')
    if expected['contract']['rules'] >= 15 and not any(row['mod_key'] == 'roottop' for row in find('data/meshes/top.nif')):
        raise ValueError('producer root strip-prefix invariant failed')
    if expected['contract']['rules'] >= 18:
        ignored = expected.get('conflict_only', [])
        if len(ignored) != 2 or any(row['conflict_status'] != 0 or not row['winning'] or not row['deployable'] for row in ignored):
            raise ValueError('producer conflict-only files were hidden or marked conflicting')


def main():
    output = Path('/output')
    report = {'report_schema': 1, 'approved': False, 'status': 'setup_error', 'readers': [],
              'limits': ['synthetic bounded Skyrim fixture', 'no gameplay, live deployment, or staged-write/race certification',
                         'passing reader parity does not approve production admission']}
    try:
        for key in ('HOME', 'XDG_CONFIG_HOME', 'XDG_STATE_HOME', 'XDG_DATA_HOME', 'XDG_CACHE_HOME'):
            Path(os.environ[key]).mkdir(parents=True, exist_ok=True)
        request = json.loads((output / 'request.json').read_text())
        report['request'] = request
        with tempfile.TemporaryDirectory(prefix='hc-compatibility-') as raw:
            root = Path(raw)
            oracle = output / 'producer-oracle.json'
            subprocess.run([sys.executable, str(Path(__file__).with_name('amethyst_compatibility_fixture.py')),
                            str(root), '/producer.so', '/amethyst', str(oracle)], check=True, timeout=180)
            expected = json.loads(oracle.read_text())
            report['producer'] = {key: expected[key] for key in ('contract', 'status', 'generation', 'inventory_generation')}
            report['transport'] = transport_contract(Path('/server/amethyst_snapshot.py'))
            producer_invariants(expected)
            report['producer_invariants'] = 'pass'
            before = fingerprint(root)
            for item in [{'name': 'producer-control', 'path': '/producer.so'}, *request['readers']]:
                entry = {'name': item['name']}
                try:
                    entry.update(compare_reader(root, expected, Path(item['path']), Path('/server/amethyst_snapshot.py')))
                except Exception as exc:
                    entry.update(status='fail', error=str(exc))
                report['readers'].append(entry)
            report['status'] = 'setup_error' if report['readers'][0]['status'] != 'pass' else 'comparison_failed'
            report['production_server'] = server_probe(root, Path('/server'))
            after = fingerprint(root)
            report['producer_inputs_unchanged'] = before == after
            report['input_changes'] = {name: {'before': before.get(name), 'after': after.get(name)}
                                       for name in before.keys() | after.keys() if before.get(name) != after.get(name)}
            if not report['producer_inputs_unchanged']:
                raise ValueError('comparison changed producer files/catalog')
            if report['readers'][0]['status'] != 'pass':
                report.update(status='setup_error', error='producer control could not validate the experiment; inspect reader and transport coverage')
            elif any(row['status'] == 'fail' for row in report['readers']):
                report['status'] = 'comparison_failed'
            else:
                report['status'] = 'comparison_complete'
    except Exception as exc:
        report['error'] = str(exc)
    (output / 'report.json').write_text(json.dumps(report, indent=2) + '\n')
    return 2 if report['status'] == 'setup_error' else 1 if report['status'] == 'comparison_failed' else 0


if __name__ == '__main__':
    sys.exit(main())
