#!/usr/bin/env python3
"""Assemble a deterministic, hash-manifested Linux x64 offline package."""
from __future__ import annotations

import argparse
import gzip
import hashlib
import json
import os
from pathlib import Path
from pathlib import PurePosixPath
import shutil
import tarfile
import tempfile


def sha256(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            h.update(block)
    return h.hexdigest()


def add_file(source: Path, stage: Path, relative: str) -> None:
    if not source.is_file() or source.is_symlink():
        raise ValueError(f"required package input is missing or not a regular file: {source}")
    destination = stage / relative
    destination.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(source, destination)
    destination.chmod(0o755 if os.access(source, os.X_OK) else 0o644)


def add_tree(source: Path, stage: Path, relative: str) -> None:
    if not source.is_dir() or source.is_symlink():
        raise ValueError(f"required package directory is missing or unsafe: {source}")
    for path in sorted(source.rglob("*")):
        relpath = path.relative_to(source)
        if "evals" in relpath.parts or path.name == "_CORPUS_STATUS.md":
            continue
        if path.is_symlink():
            raise ValueError(f"refusing symlink in packaged skills: {path}")
        if path.is_file():
            add_file(path, stage, (Path(relative) / relpath).as_posix())


def native_tool_artifacts(root: Path) -> tuple[Path, list[tuple[Path, str]]]:
    info_path = root / "native-build-info.json"
    lock_path = root / "native-tools.lock.json"
    manifest_path = root / "vcpkg.json"
    for path in (info_path, lock_path, manifest_path):
        if not path.is_file() or path.is_symlink():
            raise ValueError(f"native source-build provenance is missing or unsafe: {path}")
    try:
        info = json.loads(info_path.read_text(encoding="utf-8"))
        lock = json.loads(lock_path.read_text(encoding="utf-8"))
        vcpkg = json.loads(manifest_path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as ex:
        raise ValueError(f"native source-build provenance is invalid JSON: {ex}") from ex
    if info.get("schema") != 1 or lock.get("schema") != 1:
        raise ValueError("unsupported native source-build provenance schema")
    if info.get("libc") != {"name": "glibc", "version": "2.39"}:
        raise ValueError("native tools must be built on the Ubuntu 24.04 glibc 2.39 baseline before package assembly")
    if info.get("baseline") != lock.get("baseline") or info.get("vcpkg_commit") != lock.get("vcpkg", {}).get("commit"):
        raise ValueError("native build metadata does not match its pinned source/dependency lock")
    if vcpkg.get("builtin-baseline") != info.get("vcpkg_commit") or vcpkg.get("dependencies") != lock.get("dependencies"):
        raise ValueError("native vcpkg manifest does not match the pinned lock")
    expected_tools = {name: {key: tool[key] for key in ("commit", "version", "license")}
                      for name, tool in lock.get("tools", {}).items()}
    if info.get("tools") != expected_tools or set(expected_tools) != {"caprica", "champollion"}:
        raise ValueError("native tool versions or source commits do not match the pin file")

    artifacts = info.get("artifacts")
    if not isinstance(artifacts, list) or not artifacts:
        raise ValueError("native build metadata has no artifact manifest")
    expected = {
        "caprica", "champollion", "native-tools.lock.json", "vcpkg.json",
        "BUILD-NOTES.md", "build/build-linux-tools.py",
        f"sources/Caprica-{expected_tools['caprica']['commit']}.tar.gz",
        f"sources/Champollion-{expected_tools['champollion']['commit']}.tar.gz",
        "licenses/Caprica-MIT.txt", "licenses/Champollion-LGPL-3.0.txt",
    }
    seen: set[str] = set()
    result: list[tuple[Path, str]] = []
    dependency_licenses = 0
    for item in artifacts:
        if not isinstance(item, dict):
            raise ValueError("native build artifact entry is malformed")
        rel = item.get("path")
        if not isinstance(rel, str):
            raise ValueError("native build artifact has no path")
        pure = PurePosixPath(rel)
        if pure.is_absolute() or pure.as_posix() != rel or any(part in ("", ".", "..") for part in pure.parts):
            raise ValueError(f"unsafe native build artifact path: {rel!r}")
        if rel in seen:
            raise ValueError(f"duplicate native build artifact path: {rel}")
        seen.add(rel)
        source = root.joinpath(*pure.parts)
        if not source.is_file() or source.is_symlink():
            raise ValueError(f"native build artifact is missing or unsafe: {source}")
        if item.get("size") != source.stat().st_size or item.get("sha256") != sha256(source):
            raise ValueError(f"native build artifact hash or size mismatch: {source}")
        if item.get("mode") not in (0o644, 0o755):
            raise ValueError(f"invalid native build artifact mode: {rel}")
        if rel.startswith("licenses/vcpkg/") and rel.endswith("/copyright"):
            dependency_licenses += 1
        result.append((source, f"tools/{rel}"))
    if not expected.issubset(seen) or dependency_licenses != info.get("dependency_license_files") or dependency_licenses < 1:
        raise ValueError("native source archives, build recipe, or dependency notices are incomplete")
    result.append((info_path, "tools/native-build-info.json"))
    return info_path, result


def build(repo: Path, output: Path, native_tools: Path) -> None:
    server = repo / "dist/linux-base/server"
    required_server_files = (
        "housecarl-mcp",
        "housecarl-mcp.dll",
        "housecarl-mcp.deps.json",
        "housecarl-mcp.runtimeconfig.json",
        "libhostfxr.so",
        "libhostpolicy.so",
        "amethyst_filegraph.abi3.so",
        "amethyst_snapshot.py",
        "corpus.json",
    )
    missing_server_files = [
        name for name in required_server_files
        if not (server / name).is_file() or (server / name).is_symlink()
    ]
    if missing_server_files:
        raise ValueError(
            "self-contained server publish output is incomplete; missing: "
            + ", ".join(missing_server_files)
        )
    inputs = {
        "README.md": repo / "README.md",
        "LICENSE": repo / "LICENSE",
        "THIRD-PARTY-NOTICES.txt": repo / "THIRD-PARTY-NOTICES.txt",
        "docs/amethyst-saved-snapshot.md": repo / "docs/architecture/amethyst-saved-snapshot.md",
        "docs/linux-install.md": repo / "docs/linux-install.md",
    }
    plugin = json.loads((repo / "plugin/.claude-plugin/plugin.json").read_text(encoding="utf-8"))
    version = plugin.get("version")
    if not isinstance(version, str) or not version:
        raise ValueError("plugin manifest has no package version")
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="housecarl-linux-package-") as temp:
        stage = Path(temp)
        for relative, source in inputs.items(): add_file(source, stage, relative)
        add_tree(server, stage, "server")
        _, native_artifacts = native_tool_artifacts(native_tools)
        for source, relative in native_artifacts: add_file(source, stage, relative)
        add_tree(repo / ".claude/skills", stage, "claude/skills")
        add_tree(repo / "plugin/codex/housecarl", stage, "codex/skills/housecarl")
        files = []
        for path in sorted(p for p in stage.rglob("*") if p.is_file()):
            rel = path.relative_to(stage).as_posix()
            files.append({"path": rel, "sha256": sha256(path), "size": path.stat().st_size,
                          "mode": 0o755 if os.access(path, os.X_OK) else 0o644})
        manifest = {"schema": 1, "name": "houseCARL-Amethyst-Linux-x64", "version": version,
                    "transaction_journal_schema": 1, "runtime": "linux-x64-self-contained-net9-untrimmed",
                    "python_minimum": "3.12", "native_filegraph_api": 13, "native_filegraph_schema": 9,
                    "files": files}
        (stage / "manifest.json").write_text(json.dumps(manifest, sort_keys=True, indent=2) + "\n", encoding="utf-8")
        temp_archive = output.with_suffix(output.suffix + ".tmp")
        with temp_archive.open("wb") as raw, gzip.GzipFile(fileobj=raw, mode="wb", filename="", mtime=0) as gz:
            with tarfile.open(fileobj=gz, mode="w", format=tarfile.PAX_FORMAT) as tar:
                manifest_path = stage / "manifest.json"
                all_files = sorted(p for p in stage.rglob("*") if p.is_file() and p != manifest_path) + [manifest_path]
                for path in all_files:
                    rel = path.relative_to(stage).as_posix()
                    info = tarfile.TarInfo(rel)
                    info.size = path.stat().st_size
                    info.mode = 0o755 if os.access(path, os.X_OK) else 0o644
                    info.mtime = 0
                    with path.open("rb") as stream: tar.addfile(info, stream)
        os.replace(temp_archive, output)
    output.with_suffix(output.suffix + ".sha256").write_text(f"{sha256(output)}  {output.name}\n", encoding="ascii")
    shutil.copyfile(repo / "scripts/install-linux.py", output.parent / "install-linux.py")
    print(f"Package: {output}")
    print(f"Installer: {output.parent / 'install-linux.py'}")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", type=Path, default=Path(__file__).resolve().parent.parent)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--native-tools", type=Path, default=Path("dist/linux-tools"))
    args = parser.parse_args()
    repo = args.repo.resolve()
    output = (args.output or repo / "dist/housecarl-amethyst-linux-x64.tar.gz").resolve()
    try: build(repo, output, (repo / args.native_tools).resolve() if not args.native_tools.is_absolute() else args.native_tools)
    except (OSError, ValueError, json.JSONDecodeError) as ex:
        parser.exit(2, f"package-linux: {ex}\n")
    return 0


if __name__ == "__main__": raise SystemExit(main())
