import importlib.util
import json
from pathlib import Path
import sys
import tarfile
import tempfile
import unittest

scripts = Path(__file__).resolve().parents[1]
package_spec = importlib.util.spec_from_file_location("package_linux", scripts / "package-linux.py")
package_linux = importlib.util.module_from_spec(package_spec)
sys.modules[package_spec.name] = package_linux
package_spec.loader.exec_module(package_linux)
install_spec = importlib.util.spec_from_file_location("install_linux", scripts / "install-linux.py")
installer = importlib.util.module_from_spec(install_spec)
sys.modules[install_spec.name] = installer
install_spec.loader.exec_module(installer)


class LinuxPackageTests(unittest.TestCase):
    @staticmethod
    def write_native_tool_fixture(root: Path) -> Path:
        native = root / "dist/linux-tools"
        lock = json.loads((scripts / "native-tools.lock.json").read_text(encoding="utf-8"))
        manifest = json.loads((scripts / "vcpkg-linux-tools.json").read_text(encoding="utf-8"))
        payloads = {
            "caprica": b"caprica",
            "champollion": b"champollion",
            "native-tools.lock.json": (scripts / "native-tools.lock.json").read_bytes(),
            "vcpkg.json": (scripts / "vcpkg-linux-tools.json").read_bytes(),
            "BUILD-NOTES.md": b"pinned native tool build",
            "build/build-linux-tools.py": (scripts / "build-linux-tools.py").read_bytes(),
            f"sources/Caprica-{lock['tools']['caprica']['commit']}.tar.gz": b"caprica-source",
            f"sources/Champollion-{lock['tools']['champollion']['commit']}.tar.gz": b"champollion-source",
            "licenses/Caprica-MIT.txt": b"MIT license",
            "licenses/Champollion-LGPL-3.0.txt": b"LGPL license",
            "licenses/vcpkg/fmt/copyright": b"fmt license",
        }
        for relative, content in payloads.items():
            path = native / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(content)
            if relative in ("caprica", "champollion"):
                path.chmod(0o755)
        tools = {name: {key: value[key] for key in ("commit", "version", "license")}
                 for name, value in lock["tools"].items()}
        artifacts = []
        for relative in sorted(payloads):
            path = native / relative
            artifacts.append({"path": relative, "sha256": package_linux.sha256(path),
                              "size": path.stat().st_size,
                              "mode": 0o755 if relative in ("caprica", "champollion") else 0o644})
        info = {"schema": 1, "target": "linux-x64", "baseline": lock["baseline"],
                "libc": {"name": "glibc", "version": "2.39"},
                "compiler": "clang test", "cmake": "cmake test",
                "vcpkg_commit": lock["vcpkg"]["commit"], "vcpkg_triplet": lock["vcpkg"]["triplet"],
                "tools": tools, "dependency_license_files": 1,
                "caprica_build_patch": "test", "smoke_test": "test", "artifacts": artifacts}
        (native / "native-build-info.json").write_text(json.dumps(info, indent=2), encoding="utf-8")
        return native

    def test_archive_has_single_manifest_complete_skill_bundle_and_validated_payload(self):
        with tempfile.TemporaryDirectory(prefix="hc-linux-pkg-test-") as raw:
            root = Path(raw)
            files = {
                "dist/linux-base/server/housecarl-mcp": b"server",
                "dist/linux-base/server/amethyst_filegraph.abi3.so": b"native",
                "dist/linux-base/server/amethyst_snapshot.py": b"helper",
                "dist/linux-base/server/corpus.json": b"{}",
                "plugin/codex/housecarl/SKILL.md": b"umbrella",
                "README.md": b"readme", "LICENSE": b"license",
                "THIRD-PARTY-NOTICES.txt": b"notices",
                "docs/architecture/amethyst-saved-snapshot.md": b"snapshot",
                "docs/linux-install.md": b"install",
                ".claude/skills/example/SKILL.md": b"skill",
                ".claude/skills/example/references/data.jsonl": b"data",
                ".claude/skills/example/evals/evals.json": b"exclude",
                ".claude/skills/example/_CORPUS_STATUS.md": b"exclude",
            }
            for name, payload in files.items():
                path = root / name; path.parent.mkdir(parents=True, exist_ok=True); path.write_bytes(payload)
                if name.endswith("housecarl-mcp"): path.chmod(0o755)
            native_tools = self.write_native_tool_fixture(root)
            (root / "plugin/.claude-plugin").mkdir(parents=True)
            (root / "plugin/.claude-plugin/plugin.json").write_text(json.dumps({"version": "1.2.3"}))
            (root / "scripts").mkdir()
            (root / "scripts/install-linux.py").write_bytes((scripts / "install-linux.py").read_bytes())
            archive = root / "out/package.tar.gz"
            package_linux.build(root, archive, native_tools)
            second_archive = root / "out/package-second-build.tar.gz"
            package_linux.build(root, second_archive, native_tools)
            self.assertEqual(archive.read_bytes(), second_archive.read_bytes(), "same inputs must produce byte-identical archives")
            with tarfile.open(archive, "r:gz") as tar:
                names = [member.name for member in tar.getmembers()]
                self.assertEqual(1, names.count("manifest.json"))
                self.assertIn("claude/skills/example/references/data.jsonl", names)
                self.assertIn("codex/skills/housecarl/SKILL.md", names)
                self.assertNotIn("claude/skills/example/evals/evals.json", names)
                self.assertNotIn("claude/skills/example/_CORPUS_STATUS.md", names)
                self.assertIn("tools/sources/Caprica-" + json.loads((native_tools / "native-tools.lock.json").read_text())["tools"]["caprica"]["commit"] + ".tar.gz", names)
                self.assertIn("tools/licenses/Champollion-LGPL-3.0.txt", names)
                self.assertIn("tools/licenses/vcpkg/fmt/copyright", names)
                self.assertIn("tools/native-build-info.json", names)
            manifest, _ = installer.inspect_archive(archive)
            self.assertEqual("1.2.3", manifest["version"])

    def test_native_tool_package_refuses_modified_source_and_missing_license(self):
        with tempfile.TemporaryDirectory(prefix="hc-linux-native-integrity-") as raw:
            root = Path(raw)
            native = self.write_native_tool_fixture(root)
            source = next((native / "sources").iterdir())
            source.write_bytes(b"changed source archive")
            with self.assertRaisesRegex(ValueError, "hash or size mismatch"):
                package_linux.native_tool_artifacts(native)
            second_native = self.write_native_tool_fixture(root / "missing-license")
            (second_native / "licenses/Caprica-MIT.txt").unlink()
            with self.assertRaisesRegex(ValueError, "missing or unsafe"):
                package_linux.native_tool_artifacts(second_native)

    def test_native_tool_package_refuses_a_newer_glibc_build(self):
        with tempfile.TemporaryDirectory(prefix="hc-linux-native-baseline-") as raw:
            native = self.write_native_tool_fixture(Path(raw))
            info_path = native / "native-build-info.json"
            info = json.loads(info_path.read_text(encoding="utf-8"))
            info["libc"]["version"] = "2.44"
            info_path.write_text(json.dumps(info), encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "Ubuntu 24.04 glibc 2.39"):
                package_linux.native_tool_artifacts(native)


if __name__ == "__main__": unittest.main()
