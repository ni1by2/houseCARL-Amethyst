import importlib.util
from pathlib import Path
import sqlite3
import unittest

spec = importlib.util.spec_from_file_location('fixture_builder', Path(__file__).resolve().parents[1] / 'probes/build_amethyst_snapshot_fixture.py')
builder = importlib.util.module_from_spec(spec)
spec.loader.exec_module(builder)


class CatalogFixtureTests(unittest.TestCase):
    def test_sql_round_trip_preserves_nul_archive_keys_and_storage_types(self):
        with sqlite3.connect(':memory:') as original, sqlite3.connect(':memory:') as restored:
            original.execute('CREATE TABLE identities(key TEXT, data BLOB, rank INTEGER, label TEXT)')
            original.execute('INSERT INTO identities VALUES(?,?,?,?)', ('Beta\0fixture.bsa', b'\0\xff', 3, "owner's archive"))
            original.commit()
            restored.executescript(builder.dump_catalog(original))
            self.assertEqual(original.execute('SELECT * FROM identities').fetchall(), restored.execute('SELECT * FROM identities').fetchall())


if __name__ == '__main__':
    unittest.main()
