import copy
import importlib.util
import io
import json
from pathlib import Path
import struct
import subprocess
import tempfile
import unittest
from unittest.mock import patch

probes = Path(__file__).resolve().parents[1] / 'probes'


def load(name):
    spec = importlib.util.spec_from_file_location(name, probes / (name + '.py'))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


worker = load('amethyst_compatibility_worker')
runner = load('run_amethyst_compatibility')


class CompatibilityHarnessTests(unittest.TestCase):
    def test_runtime_selector_uses_the_published_schema(self):
        def schema(*names):
            return [{'name': 'housecarl_asset_status', 'inputSchema': {'properties': {n: {} for n in names}}}]
        self.assertEqual('paths', worker.asset_selector(schema('paths', 'asset_paths')))
        self.assertEqual('asset_paths', worker.asset_selector(schema('asset_paths')))
        for tools in [[], schema('unknown')]:
            with self.assertRaises(ValueError):
                worker.asset_selector(tools)

    def test_changed_winner_or_source_is_a_failure(self):
        original = dict(candidate_id=1, namespace='normal', target='game', mod_key='alpha',
                        destination_key=[100, 97, 116, 97], source_rel=[97])
        worker.check_winners([original], [copy.deepcopy(original)])
        for field, value in [('candidate_id', 2), ('mod_key', 'beta'), ('mod_key', 'Alpha'), ('source_rel', [98]),
                             ('namespace', 'root'), ('target', 'other'), ('destination_key', [98])]:
            with self.subTest(field=field):
                changed = dict(original, **{field: value})
                with self.assertRaises(ValueError):
                    worker.check_winners([original], [changed])
        with self.assertRaises(ValueError):
            worker.check_winners([original], [])

    def test_frames_refuse_truncation_and_unbounded_payloads(self):
        self.assertEqual(b'ab', worker.frame(io.BytesIO(struct.pack('<I', 2) + b'ab')))
        for payload in [b'', b'xx', struct.pack('<I', 0), struct.pack('<I', 65 * 1024 * 1024), struct.pack('<I', 4) + b'a']:
            with self.subTest(payload=payload[:8]):
                with self.assertRaises(ValueError):
                    worker.frame(io.BytesIO(payload))

    def test_empty_sqlite_read_sidecars_do_not_hide_data_changes(self):
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            (root / 'filegraph.sqlite3').write_bytes(b'catalog')
            original = worker.fingerprint(root)
            (root / 'filegraph.sqlite3-wal').touch()
            (root / 'filegraph.sqlite3-shm').write_bytes(b'locks')
            self.assertEqual(original, worker.fingerprint(root))
            (root / 'filegraph.sqlite3-wal').write_bytes(b'new catalog data')
            self.assertNotEqual(original, worker.fingerprint(root))
            (root / 'filegraph.sqlite3-wal').unlink()
            (root / 'filegraph.sqlite3').write_bytes(b'changed catalog')
            self.assertNotEqual(original, worker.fingerprint(root))

    def test_dirty_upstream_cannot_be_reported_as_a_release_commit(self):
        with patch.object(runner.subprocess, 'check_output', side_effect=['a' * 40, ' M src/file.py']):
            with self.assertRaisesRegex(ValueError, 'clean committed'):
                runner.source_identity(Path('/synthetic'))

    def test_wrong_reader_schema_is_not_a_parity_pass(self):
        with patch.object(worker, 'reader_metadata', return_value={'api': 13, 'schema': 9}):
            result = worker.compare_reader(Path('/unused'), {'contract': {'api': 13, 'schema': 10}}, Path('/reader'), Path('/helper'))
            self.assertEqual('not_applicable', result['status'])

    def test_producer_invariants_allow_case_preserved_keys_without_normalizing_parity(self):
        rows = [("meshes/example.nif", "Beta"), ("meshes/overwrite.nif", "[Overwrite]"),
                ("meshes/ignored.nif", "Beta"), ("meshes/ignored/folder.nif", "Beta"), ("meshes/top.nif", "RootTop")]
        for preserve in (False, True):
            winners = [{'destination_key': list(('data/' + path).encode()), 'namespace': 'normal',
                        'mod_name': mod, 'mod_key': mod if preserve else mod.lower()} for path, mod in rows]
            winners.append({'destination_key': list(b'data/archive-member'), 'namespace': 'archive', 'mod_name': 'Alpha'})
            expected = dict(winners=winners, export_matches_paging=True, contract={'rules': 19},
                            conflict_only=[dict(conflict_status=0, winning=True, deployable=True)] * 2)
            worker.producer_invariants(expected)
            winners.append({'destination_key': list(b'data/hidden'), 'namespace': 'normal', 'mod_name': 'Gamma'})
            with self.assertRaisesRegex(ValueError, 'disabled or excluded'):
                worker.producer_invariants(expected)

    def test_future_contract_is_unsupported_transport_not_reader_regression(self):
        with tempfile.TemporaryDirectory() as raw:
            helper = Path(raw) / 'helper.py'
            helper.write_text('NATIVE_API = 13\nNATIVE_SCHEMAS = (9, 10)\n')
            for contract in [{'api': 13, 'schema': 11}, {'api': 14, 'schema': 10}]:
                with self.subTest(contract=contract), patch.object(worker, 'reader_metadata', return_value=contract):
                    result = worker.compare_reader(Path('/unused'), {'contract': contract}, Path('/reader'), helper)
                    self.assertEqual('unsupported_transport', result['status'])

    def test_docker_startup_failure_returns_setup_exit_and_retains_raw_exit(self):
        with tempfile.TemporaryDirectory() as raw:
            root = Path(raw)
            source, extension, server, output = root / 'source', root / 'producer.so', root / 'server', root / 'report'
            source.mkdir(); server.mkdir(); extension.write_bytes(b'native')
            for name in ('housecarl-mcp', 'housecarl-mcp.dll', 'amethyst_snapshot.py', 'amethyst_filegraph.so'):
                (server / name).write_bytes(b'baseline')
            argv = ['suite', '--source', str(source), '--producer-extension', str(extension),
                    '--server', str(server), '--output', str(output)]
            with patch.object(runner.sys, 'argv', argv), patch.object(runner, 'source_identity', return_value='a' * 40), \
                 patch.object(runner.subprocess, 'check_output', return_value='b' * 40), \
                 patch.object(runner.subprocess, 'run', return_value=subprocess.CompletedProcess([], 125)), \
                 patch('builtins.print'):
                self.assertEqual(2, runner.main())
            report = json.loads((output / 'report.json').read_text())
            self.assertEqual('setup_error', report['status'])
            self.assertEqual(125, report['container_exit_code'])

    def test_server_failure_preserves_unsupported_control_as_setup_error(self):
        for control, status, exit_code in [('unsupported_transport', 'setup_error', 2), ('pass', 'comparison_failed', 1)]:
            with self.subTest(control=control), tempfile.TemporaryDirectory() as raw:
                root = Path(raw)
                (root / 'request.json').write_text('{"readers": []}')
                oracle = dict(contract={}, status={}, generation=1, inventory_generation=1)
                (root / 'producer-oracle.json').write_text(json.dumps(oracle))
                def path(value):
                    return root if value == '/output' else Path(value)
                with patch.object(worker, 'Path', side_effect=path), \
                     patch.dict(worker.os.environ, {key: raw for key in ('HOME', 'XDG_CONFIG_HOME', 'XDG_STATE_HOME', 'XDG_DATA_HOME', 'XDG_CACHE_HOME')}), \
                     patch.object(worker.subprocess, 'run'), patch.object(worker, 'transport_contract', return_value={}), \
                     patch.object(worker, 'producer_invariants'), patch.object(worker, 'compare_reader', return_value={'status': control}), \
                     patch.object(worker, 'server_probe', side_effect=ValueError('server unavailable')):
                    self.assertEqual(exit_code, worker.main())
                self.assertEqual(status, json.loads((root / 'report.json').read_text())['status'])

    def test_refresh_failures_cannot_be_reported_as_completed_comparisons(self):
        for refresh, status, exit_code in [('pass', 'comparison_complete', 0), ('not_applicable', 'comparison_complete', 0),
                                           ('fail', 'comparison_failed', 1), ('setup_error', 'setup_error', 2)]:
            with self.subTest(refresh=refresh), tempfile.TemporaryDirectory() as raw:
                root = Path(raw)
                (root / 'request.json').write_text('{"readers": []}')
                oracle = dict(contract={}, status={}, generation=1, inventory_generation=1)
                (root / 'producer-oracle.json').write_text(json.dumps(oracle))
                def path(value):
                    return root if value == '/output' else Path(value)
                with patch.object(worker, 'Path', side_effect=path), \
                     patch.dict(worker.os.environ, {key: raw for key in ('HOME', 'XDG_CONFIG_HOME', 'XDG_STATE_HOME', 'XDG_DATA_HOME', 'XDG_CACHE_HOME')}), \
                     patch.object(worker.subprocess, 'run'), patch.object(worker, 'transport_contract', return_value={}), \
                     patch.object(worker, 'producer_invariants'), patch.object(worker, 'compare_reader', return_value={'status': 'pass'}), \
                     patch.object(worker, 'server_probe', return_value={'status': 'accepted'}), \
                     patch.object(worker, 'refresh_lifecycle_probe', return_value={'status': refresh}):
                    self.assertEqual(exit_code, worker.main())
                report = json.loads((root / 'report.json').read_text())
                self.assertEqual(status, report['status'])
                self.assertEqual(refresh, report['refresh_lifecycle']['status'])
                self.assertFalse(report['approved'])

    def test_refresh_subprocess_must_supply_a_valid_report_and_matching_exit(self):
        for report, exit_code, expected in [({'status': 'pass'}, 0, 'pass'), ({'status': 'fail'}, 1, 'fail'),
                                            ({'status': 'not_applicable'}, 0, 'not_applicable'),
                                            ({'status': 'pass'}, 1, 'setup_error'), ({'status': 'unexpected'}, 0, 'setup_error'),
                                            ([], 0, 'setup_error'), ({'status': {}}, 0, 'setup_error'),
                                            (None, 0, 'setup_error')]:
            with self.subTest(report=report, exit_code=exit_code), tempfile.TemporaryDirectory() as raw:
                root = Path(raw)
                if report is not None:
                    (root / 'refresh-lifecycle.json').write_text(json.dumps(report))
                with patch.object(worker.subprocess, 'run', return_value=subprocess.CompletedProcess([], exit_code)):
                    self.assertEqual(expected, worker.refresh_lifecycle_probe(root)['status'])

    def test_refresh_timeout_is_a_setup_error(self):
        with tempfile.TemporaryDirectory() as raw, \
             patch.object(worker.subprocess, 'run', side_effect=subprocess.TimeoutExpired('refresh', 180)):
            self.assertEqual('setup_error', worker.refresh_lifecycle_probe(Path(raw))['status'])


if __name__ == '__main__':
    unittest.main()
