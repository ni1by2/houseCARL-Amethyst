import copy
import importlib.util
import io
from pathlib import Path
import struct
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
    def test_changed_winner_or_source_is_a_failure(self):
        original = dict(candidate_id=1, namespace='normal', target='game', mod_key='alpha',
                        destination_key=[100, 97, 116, 97], source_rel=[97])
        worker.check_winners([original], [copy.deepcopy(original)])
        for field, value in [('candidate_id', 2), ('mod_key', 'beta'), ('source_rel', [98]),
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


if __name__ == '__main__':
    unittest.main()
