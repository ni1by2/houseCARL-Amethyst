import hashlib
import importlib.util
import json
from pathlib import Path
import sys
import tarfile
import tempfile
import unittest
from unittest import mock


SCRIPT = Path(__file__).resolve().parents[1] / "install-linux.py"
spec = importlib.util.spec_from_file_location("install_linux", SCRIPT)
installer = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = installer
spec.loader.exec_module(installer)


class LinuxInstallerTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(prefix="hc-installer-test-")
        self.root = Path(self.tmp.name)
        self.state = self.root / "state"
        self.prefix = self.root / "install"
        self.home = self.root / "home"
        self.home.mkdir()
        self.home_patch = mock.patch.object(Path, "home", return_value=self.home)
        self.home_patch.start()

    def tearDown(self):
        self.home_patch.stop()
        self.tmp.cleanup()

    def package(self, version="1.0.0", corrupt=False):
        archive = self.root / f"{version}.tar.gz"
        payload = {
            "server/housecarl-mcp": b"server " + version.encode(),
            "server/amethyst_filegraph.abi3.so": b"native",
            "server/amethyst_snapshot.py": b"helper",
            "tools/caprica": b"caprica",
            "tools/champollion": b"champollion",
            "claude/skills/skyrim/SKILL.md": b"claude skill " + version.encode(),
            "codex/skills/housecarl/SKILL.md": b"codex skill " + version.encode(),
        }
        files = [{"path": name, "sha256": hashlib.sha256(data).hexdigest(), "size": len(data), "mode": 0o755 if name.endswith(("housecarl-mcp", "caprica", "champollion")) else 0o644}
                 for name, data in payload.items()]
        if corrupt: files[0]["sha256"] = "0" * 64
        manifest = {"schema": 1, "version": version, "transaction_journal_schema": 1, "files": files}
        with tarfile.open(archive, "w:gz") as tar:
            for name, data in payload.items():
                info = tarfile.TarInfo(name); info.size = len(data); info.mode = 0o755 if name.endswith(("housecarl-mcp", "caprica", "champollion")) else 0o644
                import io
                tar.addfile(info, io.BytesIO(data))
            raw = json.dumps(manifest).encode()
            info = tarfile.TarInfo("manifest.json"); info.size = len(raw); tar.addfile(info, io.BytesIO(raw))
        return archive

    def test_offline_install_upgrade_rollback_and_uninstall_preserve_state(self):
        v1 = self.package("1.0.0"); installer.install(v1, self.prefix, self.state)
        current = self.prefix / "current"
        first = current.resolve()
        self.assertIn("1.0.0", str(first))
        v2 = self.package("2.0.0"); installer.install(v2, self.prefix, self.state)
        self.assertIn("2.0.0", str(current.resolve()))
        self.assertIn("1.0.0", str((self.prefix / "previous").resolve()))
        installer.rollback(self.prefix, self.state)
        self.assertEqual(first, current.resolve())
        self.assertEqual("claude skill 1.0.0", (Path.home() / ".claude/skills/skyrim/SKILL.md").read_text())
        user_data = self.prefix / "my-notes.txt"; user_data.write_text("keep")
        state_file = self.state / "user.json"; state_file.parent.mkdir(parents=True, exist_ok=True); state_file.write_text("keep")
        installer.uninstall(self.prefix, self.state)
        self.assertEqual("keep", user_data.read_text())
        self.assertEqual("keep", state_file.read_text())
        self.assertFalse(current.exists())
        self.assertEqual([], list((self.prefix / "versions").iterdir()))

    def test_manifest_hash_mismatch_and_unsafe_version_refuse_before_install(self):
        with self.assertRaisesRegex(ValueError, "hash mismatch"):
            installer.install(self.package("1.0.0", corrupt=True), self.prefix, self.state)
        with tarfile.open(self.package("1.0.0"), "r:gz") as tar:
            pass
        archive = self.root / "traversal.tar.gz"
        manifest = {"schema": 1, "version": "../escape", "transaction_journal_schema": 1, "files": []}
        import io
        with tarfile.open(archive, "w:gz") as tar:
            raw = json.dumps(manifest).encode(); info = tarfile.TarInfo("manifest.json"); info.size = len(raw); tar.addfile(info, io.BytesIO(raw))
        with self.assertRaisesRegex(ValueError, "manifest"):
            installer.install(archive, self.prefix, self.state)
        self.assertFalse((self.root / "escape").exists())

    def test_pending_or_unknown_transaction_journals_refuse_version_switch(self):
        journal_dir = self.state / "transactions" / "tx1"; journal_dir.mkdir(parents=True)
        journal = journal_dir / "journal.json"
        journal.write_text(json.dumps({"SchemaVersion": 1, "State": "pending_deployment"}))
        installer.install(self.package("1.0.0"), self.prefix, self.state)
        journal.write_text(json.dumps({"SchemaVersion": 99, "State": "verified"}))
        with self.assertRaisesRegex(ValueError, "journal schema"):
            installer.install(self.package("2.0.0"), self.prefix, self.state)
        journal.write_text(json.dumps({"SchemaVersion": 1, "State": "future_state"}))
        with self.assertRaisesRegex(ValueError, "cannot interpret"):
            installer.install(self.package("3.0.0"), self.prefix, self.state)
        journal.write_text("not json")
        with self.assertRaisesRegex(ValueError, "unreadable"):
            installer.install(self.package("4.0.0"), self.prefix, self.state)

    def test_symlinked_versions_paths_are_never_followed_or_removed(self):
        outside = self.root / "outside"; outside.mkdir(); (outside / "keep.txt").write_text("keep")
        self.prefix.mkdir()
        (self.prefix / "versions").symlink_to(outside, target_is_directory=True)
        with self.assertRaisesRegex(ValueError, "symlink"):
            installer.install(self.package(), self.prefix, self.state)
        with self.assertRaisesRegex(ValueError, "real directory|symlink"):
            installer.rollback(self.prefix, self.state)
        with self.assertRaisesRegex(ValueError, "real directory|symlink"):
            installer.uninstall(self.prefix, self.state)
        self.assertEqual("keep", (outside / "keep.txt").read_text())

    def test_uninstall_preflights_version_entries_before_host_or_activation_changes(self):
        with mock.patch.object(Path, "home", return_value=self.home):
            installer.install(self.package(), self.prefix, self.state)
            current = self.prefix / "current"
            current_before = current.readlink()
            config = self.home / ".claude.json"
            config_before = config.read_text()
            outside = self.root / "outside"; outside.mkdir()
            (self.prefix / "versions/evil").symlink_to(outside, target_is_directory=True)
            with self.assertRaisesRegex(ValueError, "unsafe version entry"):
                installer.uninstall(self.prefix, self.state)
            self.assertEqual(current_before, current.readlink())
            self.assertEqual(config_before, config.read_text())
            self.assertTrue((self.prefix / "versions/evil").is_symlink())

    def test_uninstall_refuses_to_remove_unowned_files_inside_a_version(self):
        with mock.patch.object(Path, "home", return_value=self.home):
            installer.install(self.package(), self.prefix, self.state)
            current_before = (self.prefix / "current").readlink()
            extra = self.prefix / "current/user-notes.txt"
            extra.write_text("keep this file")
            with self.assertRaisesRegex(ValueError, "unowned or missing paths"):
                installer.uninstall(self.prefix, self.state)
            self.assertEqual(current_before, (self.prefix / "current").readlink())
            self.assertEqual("keep this file", extra.read_text())
            self.assertTrue((self.home / ".claude.json").is_file())

    def test_uninstall_refuses_symlinked_directories_inside_a_version(self):
        with mock.patch.object(Path, "home", return_value=self.home):
            installer.install(self.package(), self.prefix, self.state)
            outside = self.root / "outside"
            outside.mkdir()
            (outside / "keep.txt").write_text("outside data")
            (self.prefix / "current/user-data").symlink_to(outside, target_is_directory=True)
            with self.assertRaisesRegex(ValueError, "unsafe directory"):
                installer.uninstall(self.prefix, self.state)
            self.assertEqual("outside data", (outside / "keep.txt").read_text())
            self.assertTrue((self.prefix / "current").is_symlink())

    def test_activation_does_not_remove_unrelated_fixed_new_path(self):
        target = self.root / "target"; target.mkdir()
        collision = self.root / "current.new"; collision.write_text("keep")
        link = self.root / "current"
        installer.atomic_link(link, "target")
        self.assertEqual("keep", collision.read_text())
        self.assertEqual("target", link.readlink().as_posix())

    def test_running_server_detection_is_scoped_to_this_install(self):
        proc = self.root / "proc"; proc.mkdir()
        server = self.prefix / "versions/1.0.0/server/housecarl-mcp"
        server.parent.mkdir(parents=True); server.write_text("server")
        process = proc / "123"; process.mkdir()
        (process / "exe").symlink_to(server)
        self.assertEqual(["123"], installer.running_server_processes(self.prefix, proc))

    def test_installer_lock_refuses_a_concurrent_activation(self):
        package = self.package()
        with installer.installer_lock(self.state):
            with self.assertRaisesRegex(ValueError, "operation is in progress"):
                installer.install(package, self.prefix, self.state)
        self.assertFalse((self.prefix / "current").exists())

    def test_locked_upgrade_leaves_previous_installation_untouched(self):
        with mock.patch.object(Path, "home", return_value=self.home):
            installer.install(self.package("1.0.0"), self.prefix, self.state)
            current = (self.prefix / "current").readlink()
            before = {p.relative_to(self.prefix / "versions/1.0.0"): p.read_bytes()
                      for p in (self.prefix / "versions/1.0.0").rglob("*") if p.is_file()}
            config = (self.home / ".claude.json").read_bytes()
            with installer.installer_lock(self.state):
                with self.assertRaisesRegex(ValueError, "operation is in progress"):
                    installer.install(self.package("2.0.0"), self.prefix, self.state)
            self.assertEqual(current, (self.prefix / "current").readlink())
            self.assertFalse((self.prefix / "versions/2.0.0").exists())
            self.assertEqual(config, (self.home / ".claude.json").read_bytes())
            self.assertEqual(before, {p.relative_to(self.prefix / "versions/1.0.0"): p.read_bytes()
                                    for p in (self.prefix / "versions/1.0.0").rglob("*") if p.is_file()})

    def test_readonly_unrelated_skill_survives_host_registration(self):
        with mock.patch.object(Path, "home", return_value=self.home):
            installer.install(self.package("1.0.0"), self.prefix, self.state)
            leftover = self.home / ".claude/skills/stuck-skill"
            leftover.mkdir()
            text = leftover / "SKILL.md"
            text.write_text("unrelated user skill")
            leftover.chmod(0o555)
            try:
                with self.assertRaises(PermissionError):
                    (leftover / "cannot-write").write_text("must fail")
                installer.install(self.package("2.0.0"), self.prefix, self.state)
                self.assertEqual("unrelated user skill", text.read_text())
                self.assertEqual("2.0.0", (self.prefix / "current").resolve().name)
                self.assertIn("housecarl", (self.home / ".claude.json").read_text())
                self.assertIn("housecarl", (self.home / ".codex/config.toml").read_text())
            finally:
                leftover.chmod(0o755)

    def test_atomic_configuration_write_preserves_existing_mode_and_defaults_private(self):
        existing = self.home / "existing.json"
        existing.write_text("before")
        existing.chmod(0o640)
        installer.atomic_text(existing, "after")
        self.assertEqual("after", existing.read_text())
        self.assertEqual(0o640, existing.stat().st_mode & 0o777)
        created = self.home / "new.json"
        installer.atomic_text(created, "private")
        self.assertEqual(0o600, created.stat().st_mode & 0o777)

    def test_install_refuses_regular_previous_without_current(self):
        self.prefix.mkdir()
        (self.prefix / "previous").write_text("user file")
        with self.assertRaisesRegex(ValueError, "non-symlink activation"):
            installer.install(self.package(), self.prefix, self.state)
        self.assertEqual("user file", (self.prefix / "previous").read_text())

    def test_install_refuses_current_unowned_target_without_changing_activation(self):
        versions = self.prefix / "versions"; versions.mkdir(parents=True)
        unowned = versions / "unowned"; unowned.mkdir()
        current = self.prefix / "current"; current.symlink_to("versions/unowned")
        with self.assertRaisesRegex(ValueError, "ownership manifest"):
            installer.install(self.package(), self.prefix, self.state)
        self.assertEqual("versions/unowned", current.readlink().as_posix())
        self.assertFalse((self.prefix / "previous").exists())

    def test_failed_host_registration_restores_previous_activation(self):
        with mock.patch.object(Path, "home", return_value=self.home):
            installer.install(self.package("1.0.0"), self.prefix, self.state)
            current = self.prefix / "current"
            previous = self.prefix / "previous"
            old_current, old_previous = current.readlink(), previous.readlink() if previous.is_symlink() else None
            with mock.patch.object(installer, "register_hosts", side_effect=ValueError("injected registration failure")):
                with self.assertRaisesRegex(ValueError, "injected registration failure"):
                    installer.install(self.package("2.0.0"), self.prefix, self.state)
            self.assertEqual(old_current, current.readlink())
            self.assertEqual(old_previous, previous.readlink() if previous.is_symlink() else None)
            self.assertFalse((self.prefix / "versions/2.0.0").exists())

    def test_codex_config_failure_restores_claude_and_skill_updates(self):
        with mock.patch.object(Path, "home", return_value=self.home):
            installer.install(self.package("1.0.0"), self.prefix, self.state)
            claude_path = self.home / ".claude.json"
            codex_path = self.home / ".codex/config.toml"
            claude_before = claude_path.read_text()
            codex_before = codex_path.read_text()
            skill_before = (self.home / ".claude/skills/skyrim/SKILL.md").read_text()
            real_atomic_text = installer.atomic_text
            def fail_codex(path, text):
                if path == codex_path: raise OSError("injected Codex write failure")
                return real_atomic_text(path, text)
            with mock.patch.object(installer, "atomic_text", side_effect=fail_codex):
                with self.assertRaisesRegex(OSError, "injected Codex"):
                    installer.install(self.package("2.0.0"), self.prefix, self.state)
            self.assertEqual("1.0.0", (self.prefix / "current").resolve().name)
            self.assertEqual(claude_before, claude_path.read_text())
            self.assertEqual(codex_before, codex_path.read_text())
            self.assertEqual(skill_before, (self.home / ".claude/skills/skyrim/SKILL.md").read_text())

    def test_install_registers_hosts_and_uninstall_preserves_other_settings(self):
        home = self.root / "home"; home.mkdir(exist_ok=True)
        (home / ".claude.json").write_text(json.dumps({"theme": "dark", "mcpServers": {"other": {"command": "other"}}}))
        codex = home / ".codex/config.toml"; codex.parent.mkdir()
        codex.write_text("model = 'keep'\n\n[mcp_servers.other]\ncommand = 'other'\n")
        claude_backup = home / ".claude.json.houseCARL.bak"; claude_backup.write_text("keep claude backup")
        codex_backup = home / ".codex/config.toml.houseCARL.bak"; codex_backup.write_text("keep codex backup")
        with mock.patch.object(Path, "home", return_value=home):
            installer.install(self.package(), self.prefix, self.state)
            self.assertEqual("keep claude backup", claude_backup.read_text())
            self.assertEqual("keep codex backup", codex_backup.read_text())
            claude = json.loads((home / ".claude.json").read_text())
            self.assertEqual("dark", claude["theme"])
            self.assertEqual("other", claude["mcpServers"]["other"]["command"])
            self.assertIn("current/server/housecarl-mcp", claude["mcpServers"]["housecarl"]["command"])
            self.assertEqual("keep", codex.read_text().splitlines()[0].removeprefix("model = '").removesuffix("'"))
            self.assertIn("[mcp_servers.other]", codex.read_text())
            self.assertTrue((home / ".claude/skills/skyrim/SKILL.md").is_file())
            self.assertTrue((home / ".agents/skills/housecarl/SKILL.md").is_file())
            claude["mcpServers"]["housecarl"]["custom"] = "keep"
            (home / ".claude.json").write_text(json.dumps(claude))
            codex.write_text(codex.read_text() + "enabled = true\n")
            installer.install(self.package("2.0.0"), self.prefix, self.state)
            claude = json.loads((home / ".claude.json").read_text())
            self.assertEqual("keep", claude["mcpServers"]["housecarl"]["custom"])
            self.assertIn("enabled = true", codex.read_text())
            installer.rollback(self.prefix, self.state)
            installer.uninstall(self.prefix, self.state)
            claude = json.loads((home / ".claude.json").read_text())
            self.assertEqual("keep", claude["mcpServers"]["housecarl"]["custom"])
            self.assertEqual({"other": {"command": "other"}}, {k: v for k, v in claude["mcpServers"].items() if k == "other"})
            self.assertIn("[mcp_servers.other]", codex.read_text())
            self.assertIn("[mcp_servers.housecarl]", codex.read_text())
            self.assertNotIn("command = \"" + str((self.prefix / "current/server/housecarl-mcp").absolute()), codex.read_text())
            self.assertIn("enabled = true", codex.read_text())
            self.assertFalse((home / ".claude/skills/skyrim").exists())
            self.assertFalse((home / ".agents/skills/housecarl").exists())


if __name__ == "__main__": unittest.main()
