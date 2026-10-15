#!/usr/bin/env python3
"""Build pinned Linux Papyrus tools and stage sources, licenses, and provenance."""
from __future__ import annotations

import gzip
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import platform
import re
import shutil
import subprocess
import sys
import tempfile
from typing import Any


REPO = Path(__file__).resolve().parents[1]
LOCK_PATH = REPO / "scripts/native-tools.lock.json"
VCPKG_MANIFEST_PATH = REPO / "scripts/vcpkg-linux-tools.json"
OUTPUT = REPO / "dist/linux-tools"
VCPKG_DOWNLOADS = REPO / "dist/vcpkg-downloads"


def run(args: list[str], *, cwd: Path | None = None, env: dict[str, str] | None = None,
        capture: bool = False, timeout: int = 1800) -> subprocess.CompletedProcess[str]:
    return subprocess.run(args, cwd=cwd, env=env, check=True, text=True,
                          stdout=subprocess.PIPE if capture else None,
                          stderr=subprocess.STDOUT if capture else None, timeout=timeout)


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def require_tools() -> None:
    if sys.version_info < (3, 12):
        raise RuntimeError("Python 3.12 or newer is required")
    needed = ("git", "cmake", "ninja", "clang", "clang++", "curl", "zip", "unzip", "tar")
    missing = [name for name in needed if shutil.which(name) is None]
    if missing:
        raise RuntimeError("missing build tools: " + ", ".join(missing))


def checkout(commit: str, url: str, destination: Path) -> Path:
    destination.mkdir(parents=True)
    run(["git", "init", str(destination)])
    run(["git", "-C", str(destination), "remote", "add", "origin", url])
    run(["git", "-C", str(destination), "fetch", "--depth=1", "origin", commit], timeout=600)
    run(["git", "-C", str(destination), "checkout", "--detach", "FETCH_HEAD"])
    actual = run(["git", "-C", str(destination), "rev-parse", "HEAD"], capture=True).stdout.strip()
    if actual.lower() != commit.lower():
        raise RuntimeError(f"fetched {url} at {actual}; expected pinned commit {commit}")
    return destination


def gzip_source(source: Path, commit: str, archive: Path) -> None:
    raw = subprocess.run(["git", "-C", str(source), "archive", "--format=tar",
                          "--prefix=" + source.name + "/", commit],
                         check=True, stdout=subprocess.PIPE).stdout
    archive.parent.mkdir(parents=True, exist_ok=True)
    with archive.open("wb") as file:
        with gzip.GzipFile(filename="", mode="wb", fileobj=file, mtime=0, compresslevel=9) as gz:
            gz.write(raw)


def version_line(command: list[str]) -> str:
    result = run(command, capture=True, timeout=30)
    return result.stdout.splitlines()[0].strip() if result.stdout else "unknown"


def cmake_build(source: Path, build: Path, deps: Path, vcpkg: Path, compiler: str) -> None:
    build_root = Path(os.path.commonpath((source, build, deps, vcpkg)))
    run([
        "cmake", "-S", str(source), "-B", str(build), "-G", "Ninja",
        "-DCMAKE_BUILD_TYPE=Release",
        f"-DCMAKE_CXX_COMPILER={compiler}",
        f"-DCMAKE_CXX_FLAGS=-ffile-prefix-map={build_root}=/housecarl-build",
        f"-DCMAKE_TOOLCHAIN_FILE={vcpkg / 'scripts/buildsystems/vcpkg.cmake'}",
        "-DVCPKG_TARGET_TRIPLET=x64-linux",
        f"-DVCPKG_INSTALLED_DIR={deps / 'vcpkg_installed'}",
        "-DVCPKG_MANIFEST_MODE=OFF",
    ], timeout=300)
    run(["cmake", "--build", str(build), "--parallel", str(max(1, min(os.cpu_count() or 2, 6)))], timeout=1800)


def patch_caprica_object_length(source: Path) -> None:
    """Correct the pinned writer's object-size field at source, retaining strict PEX validation."""
    header = source / "caprica/pex/PexWriter.h"
    original = "    *objectLength = (uint32_t)(strm.totalAllocatedBytes() - objectStartSize);"
    corrected = (
        "    const auto length = (uint32_t)(strm.totalAllocatedBytes() - objectStartSize + sizeof(uint32_t));\n"
        "    *objectLength = endianness == Endianness::Little ? length : byteswap(length);"
    )
    text = header.read_text(encoding="utf-8")
    if text.count(original) != 1:
        raise RuntimeError("pinned Caprica object-length patch no longer matches exactly once")
    header.write_text(text.replace(original, corrected), encoding="utf-8")


def smoke_test(caprica: Path, champollion: Path, root: Path) -> None:
    caprica_probe = subprocess.run([str(caprica)], text=True, stdout=subprocess.PIPE,
                                   stderr=subprocess.STDOUT, timeout=20, check=False)
    banner = caprica_probe.stdout
    if not re.search(r"Caprica Papyrus Compiler\s+v?0\.3\.0", banner):
        raise RuntimeError("built Caprica did not report the pinned 0.3.0 version")
    champ_probe = run([str(champollion), "--version"], capture=True, timeout=20)
    if champ_probe.returncode != 0 or "1.3.2" not in champ_probe.stdout:
        raise RuntimeError("built Champollion did not report the pinned 1.3.2 version")

    smoke = root / "smoke"
    imports, output, decompiled = smoke / "imports", smoke / "compiled", smoke / "decompiled"
    imports.mkdir(parents=True)
    output.mkdir(parents=True)
    decompiled.mkdir(parents=True)
    flags = smoke / "Skyrim.flg"
    flags.write_text("flag Hidden 0 { script property }\nflag Conditional 1 { script variable }\n", encoding="utf-8")
    config = smoke / "caprica.cfg"
    config.write_text("", encoding="utf-8")
    source = smoke / "HcBuildProbe.psc"
    source.write_text(
        "ScriptName HcBuildProbe\n"
        "Int Function Add(Int left, Int right) Global\n"
        "    return left + right\n"
        "EndFunction\n", encoding="utf-8")
    run([str(caprica), "--game=skyrim", f"--flags={flags}", f"--import={imports}",
         f"--output={output}", f"--config-file={config}", "--ignorecwd",
         "--enable-language-extensions=false", str(source)], capture=True, timeout=60)
    pex = output / "HcBuildProbe.pex"
    if not pex.is_file() or pex.stat().st_size == 0:
        raise RuntimeError("Caprica smoke compile did not produce HcBuildProbe.pex")
    run([str(champollion), "--psc", str(decompiled), str(pex)], capture=True, timeout=60)
    psc = decompiled / "HcBuildProbe.psc"
    if not psc.is_file() or "Function Add(" not in psc.read_text(encoding="utf-8", errors="replace"):
        raise RuntimeError("Champollion smoke decompile did not reconstruct the synthetic function")


def safe_relative(value: str) -> PurePosixPath:
    path = PurePosixPath(value)
    if path.is_absolute() or not path.parts or any(part in ("", ".", "..") for part in path.parts):
        raise ValueError(f"unsafe native-tool artifact path: {value!r}")
    return path


def write_atomic(path: Path, payload: bytes, mode: int = 0o644) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    if path.is_symlink() or (path.exists() and not path.is_file()):
        raise RuntimeError(f"refusing to replace a non-file native-tool output: {path}")
    fd, name = tempfile.mkstemp(prefix=f".{path.name}.", suffix=".tmp", dir=path.parent)
    try:
        with os.fdopen(fd, "wb") as stream:
            stream.write(payload)
            stream.flush()
            os.fsync(stream.fileno())
        os.chmod(name, mode)
        os.replace(name, path)
    finally:
        try: os.unlink(name)
        except FileNotFoundError: pass


def write_output(relative: str, payload: bytes, mode: int = 0o644) -> None:
    rel = safe_relative(relative)
    root = OUTPUT
    for path in (REPO / "dist", root):
        if path.is_symlink() or (path.exists() and not path.is_dir()):
            raise RuntimeError(f"refusing unsafe native-tool output directory: {path}")
        path.mkdir(parents=True, exist_ok=True)
    parent = root
    for part in rel.parts[:-1]:
        parent = parent / part
        if parent.is_symlink() or (parent.exists() and not parent.is_dir()):
            raise RuntimeError(f"refusing unsafe native-tool output path: {parent}")
        parent.mkdir(exist_ok=True)
    write_atomic(root.joinpath(*rel.parts), payload, mode)


def main() -> int:
    try:
        require_tools()
        lock: dict[str, Any] = json.loads(LOCK_PATH.read_text(encoding="utf-8"))
        manifest = json.loads(VCPKG_MANIFEST_PATH.read_text(encoding="utf-8"))
        vcpkg = lock["vcpkg"]
        if manifest.get("builtin-baseline") != vcpkg["commit"] or manifest.get("dependencies") != lock["dependencies"]:
            raise RuntimeError("vcpkg manifest and native-tools lock disagree")
        compiler = shutil.which("clang++")
        if compiler is None:
            raise RuntimeError("clang++ was not found")
        if (REPO / "dist").is_symlink() or (REPO / "dist").exists() and not (REPO / "dist").is_dir():
            raise RuntimeError(f"refusing unsafe build artifact directory: {REPO / 'dist'}")
        if VCPKG_DOWNLOADS.is_symlink() or (VCPKG_DOWNLOADS.exists() and not VCPKG_DOWNLOADS.is_dir()):
            raise RuntimeError(f"refusing unsafe vcpkg download cache: {VCPKG_DOWNLOADS}")
        VCPKG_DOWNLOADS.mkdir(parents=True, exist_ok=True)

        with tempfile.TemporaryDirectory(prefix="housecarl-linux-tools-") as raw:
            work = Path(raw)
            sources = work / "sources"
            caprica_source = checkout(lock["tools"]["caprica"]["commit"], lock["tools"]["caprica"]["repository"], sources / "Caprica")
            champ_source = checkout(lock["tools"]["champollion"]["commit"], lock["tools"]["champollion"]["repository"], sources / "Champollion")
            vcpkg_root = checkout(vcpkg["commit"], vcpkg["repository"], work / "vcpkg")
            env = os.environ.copy()
            env["VCPKG_DISABLE_METRICS"] = "1"
            env["VCPKG_DOWNLOADS"] = str(VCPKG_DOWNLOADS)
            env["CC"] = shutil.which("clang") or "clang"
            env["CXX"] = compiler
            run([str(vcpkg_root / "bootstrap-vcpkg.sh"), "-disableMetrics"], env=env, timeout=600)

            deps = work / "dependencies"
            deps.mkdir()
            (deps / "vcpkg.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
            run([str(vcpkg_root / "vcpkg"), "install", "--triplet", vcpkg["triplet"],
                 f"--x-manifest-root={deps}"], env=env, timeout=3600)

            # The pinned source spells this path with different case than the checked-in directory.
            (caprica_source / "Caprica").symlink_to("caprica", target_is_directory=True)
            patch_caprica_object_length(caprica_source)
            caprica_build, champ_build = work / "build-caprica", work / "build-champollion"
            cmake_build(caprica_source, caprica_build, deps, vcpkg_root, compiler)
            cmake_build(champ_source, champ_build, deps, vcpkg_root, compiler)
            caprica = caprica_build / "Caprica/Caprica"
            champollion = champ_build / "Champollion/Champollion"
            if not caprica.is_file() or not champollion.is_file():
                raise RuntimeError("one of the pinned native tool builds did not produce its executable")
            smoke_test(caprica, champollion, work)

            stage = work / "artifacts"
            stage.mkdir()
            for name, binary in (("caprica", caprica), ("champollion", champollion)):
                shutil.copyfile(binary, stage / name)
                (stage / name).chmod(0o755)
            source_archives = stage / "sources"
            gzip_source(caprica_source, lock["tools"]["caprica"]["commit"],
                        source_archives / f"Caprica-{lock['tools']['caprica']['commit']}.tar.gz")
            gzip_source(champ_source, lock["tools"]["champollion"]["commit"],
                        source_archives / f"Champollion-{lock['tools']['champollion']['commit']}.tar.gz")

            license_dir = stage / "licenses"
            license_dir.mkdir()
            shutil.copyfile(caprica_source / "LICENSE", license_dir / "Caprica-MIT.txt")
            shutil.copyfile(champ_source / "LICENSE", license_dir / "Champollion-LGPL-3.0.txt")
            installed_share = deps / "vcpkg_installed" / vcpkg["triplet"] / "share"
            dependency_license_count = 0
            for license in sorted(installed_share.glob("*/copyright")):
                package = license.parent.name
                if license.is_symlink() or not license.is_file():
                    raise RuntimeError(f"invalid vcpkg license file: {license}")
                relative = Path("licenses/vcpkg") / package / "copyright"
                destination = stage / relative
                destination.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(license, destination)
                dependency_license_count += 1
            if dependency_license_count == 0:
                raise RuntimeError("vcpkg did not provide any dependency license files")

            for source, relative in ((LOCK_PATH, Path("native-tools.lock.json")),
                                     (VCPKG_MANIFEST_PATH, Path("vcpkg.json")),
                                     (REPO / "docs/linux-native-tools.md", Path("BUILD-NOTES.md")),
                                     (Path(__file__).resolve(), Path("build/build-linux-tools.py"))):
                destination = stage / relative
                destination.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(source, destination)
            artifacts = []
            for path in sorted(p for p in stage.rglob("*") if p.is_file()):
                relative = path.relative_to(stage).as_posix()
                artifacts.append({"path": relative, "sha256": sha256(path), "size": path.stat().st_size,
                                  "mode": 0o755 if os.access(path, os.X_OK) else 0o644})
            info = {
                "schema": 1,
                "target": "linux-x64",
                "baseline": lock["baseline"],
                "libc": {"name": platform.libc_ver()[0], "version": platform.libc_ver()[1]},
                "compiler": version_line([compiler, "--version"]),
                "cmake": version_line(["cmake", "--version"]),
                "vcpkg_commit": vcpkg["commit"],
                "vcpkg_triplet": vcpkg["triplet"],
                "tools": {name: {"commit": data["commit"], "version": data["version"], "license": data["license"]}
                          for name, data in lock["tools"].items()},
                "dependency_license_files": dependency_license_count,
                "caprica_build_patch": "temporary Caprica -> caprica CMake symlink; PexWriter.h object length includes its own field and follows output endianness",
                "smoke_test": "Caprica compiled synthetic Papyrus source and Champollion decompiled the produced PEX",
                "artifacts": artifacts,
            }
            write_output("native-build-info.json", (json.dumps(info, indent=2, sort_keys=True) + "\n").encode())
            for item in artifacts:
                source = stage.joinpath(*safe_relative(item["path"]).parts)
                write_output(item["path"], source.read_bytes(), item["mode"])
        print(f"Built pinned Caprica and Champollion; source, notices, and build metadata are in {OUTPUT}")
        print(f"Targeted build baseline: {lock['baseline']}; host libc: {platform.libc_ver()[0]} {platform.libc_ver()[1]}")
        return 0
    except (OSError, ValueError, KeyError, RuntimeError, subprocess.CalledProcessError, subprocess.TimeoutExpired) as ex:
        print(f"build-linux-tools: {ex}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
