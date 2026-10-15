#!/usr/bin/env python3
"""Install, switch back, or remove an offline houseCARL Amethyst Linux package."""
from __future__ import annotations

import argparse
from contextlib import contextmanager
import fcntl
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import secrets
import shutil
import stat
import sys
import tarfile
import tempfile

OWNED = ".housecarl-owned.json"
RECEIPT_STATES = {"preparing", "prepared", "committing", "partial", "recovery_required"}
KNOWN_STATES = RECEIPT_STATES | {"completed", "pending_deployment", "verified", "rolled_back", "aborted"}
MAX_PAYLOAD_BYTES = 2 * 1024 * 1024 * 1024


def safe_rel(name: str) -> str:
    if not isinstance(name, str): raise ValueError("package path must be a string")
    p = PurePosixPath(name)
    if not name or p.is_absolute() or any(part in ("", ".", "..") for part in name.split("/")) or "\\" in name:
        raise ValueError(f"unsafe package path: {name!r}")
    return p.as_posix()


def hash_file(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for block in iter(lambda: f.read(1024 * 1024), b""):
            h.update(block)
    return h.hexdigest()


def inspect_archive(archive: Path) -> tuple[dict, dict[str, tarfile.TarInfo]]:
    with tarfile.open(archive, "r:gz") as tar:
        members: dict[str, tarfile.TarInfo] = {}
        for item in tar.getmembers():
            name = safe_rel(item.name.rstrip("/"))
            if name in members:
                raise ValueError(f"duplicate package path: {name}")
            if not (item.isfile() or item.isdir()):
                raise ValueError(f"package contains a link or special file: {name}")
            if item.mode & 0o6000:
                raise ValueError(f"package contains setuid/setgid mode: {name}")
            members[name] = item
        manifest_member = members.get("manifest.json")
        if manifest_member is None or not manifest_member.isfile():
            raise ValueError("package has no manifest.json")
        stream = tar.extractfile(manifest_member)
        if stream is None:
            raise ValueError("cannot read package manifest")
        manifest = json.load(stream)
        if not isinstance(manifest, dict): raise ValueError("package manifest root must be an object")
        if type(manifest.get("schema")) is not int or manifest["schema"] != 1 or not isinstance(manifest.get("version"), str) or not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._+-]{0,127}", manifest["version"]):
            raise ValueError("unsupported or incomplete package manifest")
        if not isinstance(manifest.get("files"), list):
            raise ValueError("package manifest files must be a list")
        listed: dict[str, dict] = {}
        folded: set[str] = set()
        total = 0
        for entry in manifest.get("files", []):
            if not isinstance(entry, dict): raise ValueError("package file entry must be an object")
            rel = safe_rel(entry.get("path", ""))
            if rel.casefold() in folded: raise ValueError(f"case-colliding package path: {rel}")
            folded.add(rel.casefold())
            if rel == "manifest.json" or rel in listed or rel not in members or not members[rel].isfile():
                raise ValueError(f"manifest has a duplicate or missing file entry: {rel}")
            if not isinstance(entry.get("sha256"), str) or not re.fullmatch(r"[0-9a-fA-F]{64}", entry["sha256"]):
                raise ValueError(f"manifest has an invalid SHA-256 for {rel}")
            if type(entry.get("size")) is not int or entry["size"] < 0 or entry["size"] != members[rel].size:
                raise ValueError(f"manifest has an invalid size for {rel}")
            mode = entry.get("mode")
            if type(mode) is not int or mode not in (0o644, 0o755):
                raise ValueError(f"manifest has an unsupported file mode for {rel}")
            total += entry["size"]
            if total > MAX_PAYLOAD_BYTES: raise ValueError("package payload exceeds the 2 GiB safety limit")
            listed[rel] = entry
        payload_files = {name for name, member in members.items() if member.isfile() and name != "manifest.json"}
        if payload_files != set(listed):
            raise ValueError("manifest does not describe the complete package payload")
        for rel, entry in listed.items():
            stream = tar.extractfile(members[rel])
            digest = hashlib.sha256()
            if stream is None: raise ValueError(f"cannot read package payload: {rel}")
            for block in iter(lambda: stream.read(1024 * 1024), b""): digest.update(block)
            if digest.hexdigest() != entry["sha256"].lower():
                raise ValueError(f"package payload hash mismatch: {rel}")
        if not {"server/housecarl-mcp", "server/amethyst_filegraph.abi3.so", "server/amethyst_snapshot.py",
                "tools/caprica", "tools/champollion"}.issubset(listed):
            raise ValueError("package is missing the Linux server, Amethyst snapshot helper, or pinned Papyrus tools")
        if type(manifest.get("transaction_journal_schema")) is not int or manifest["transaction_journal_schema"] <= 0:
            raise ValueError("manifest has no transaction journal schema compatibility")
        return manifest, members


def check_journal_compatibility(manifest: dict, state_root: Path) -> None:
    tx_root = state_root / "transactions"
    if state_root.is_symlink() or tx_root.is_symlink(): raise ValueError("transaction state path contains a symlink")
    if not tx_root.exists():
        return
    if tx_root.is_symlink() or not tx_root.is_dir(): raise ValueError(f"transaction directory is not a real directory: {tx_root}")
    for tx_dir in tx_root.iterdir():
        if tx_dir.is_symlink() or not tx_dir.is_dir(): raise ValueError(f"unexpected entry in transaction directory: {tx_dir}")
        journal_path = tx_dir / "journal.json"
        if not journal_path.is_file() or journal_path.is_symlink():
            raise ValueError(f"transaction record is missing or unsafe: {journal_path}")
        try:
            data = json.loads(journal_path.read_text(encoding="utf-8"))
            if not isinstance(data, dict): raise ValueError("journal root must be an object")
            schema = data.get("SchemaVersion")
            raw_state = data.get("State")
            if not isinstance(raw_state, str): raise ValueError("journal State must be a string")
            state = raw_state.lower()
        except (OSError, json.JSONDecodeError, ValueError) as ex:
            raise ValueError(f"cannot safely switch versions while transaction journal {journal_path} is unreadable: {ex}") from ex
        supported = manifest["transaction_journal_schema"]
        if type(schema) is not int or schema <= 0 or schema > supported:
            raise ValueError(f"package cannot read transaction journal {journal_path}; journal schema={schema}, package supports={supported}")
        if state not in KNOWN_STATES:
            raise ValueError(f"package cannot interpret transaction state {state!r} in {journal_path}")
        if state in RECEIPT_STATES:
            raise ValueError(f"cannot switch package versions while transaction {journal_path.parent.name} is in state {state}; recover or finish it first")


def atomic_link(path: Path, target: str) -> None:
    tmp = path.with_name(path.name + ".new-" + secrets.token_hex(12))
    created = False
    try:
        tmp.symlink_to(target)
        created = True
        os.replace(tmp, path)
    finally:
        if created: tmp.unlink(missing_ok=True)


def atomic_text(path: Path, text: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    if path.is_symlink(): raise ValueError(f"refusing to replace symlinked configuration path: {path}")
    mode = 0o600
    if path.exists():
        info = path.stat()
        if not stat.S_ISREG(info.st_mode): raise ValueError(f"configuration path is not a regular file: {path}")
        mode = stat.S_IMODE(info.st_mode)
    tmp = path.with_name(path.name + ".housecarl-new-" + secrets.token_hex(8))
    fd = os.open(tmp, os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_CLOEXEC", 0) |
                 getattr(os, "O_NOFOLLOW", 0), 0o600)
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as stream:
            os.fchmod(stream.fileno(), mode)
            stream.write(text)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(tmp, path)
        dir_fd = os.open(path.parent, os.O_RDONLY | getattr(os, "O_DIRECTORY", 0) | getattr(os, "O_CLOEXEC", 0))
        try: os.fsync(dir_fd)
        finally: os.close(dir_fd)
    finally:
        tmp.unlink(missing_ok=True)


@contextmanager
def installer_lock(state_root: Path):
    """Serialize package activation and host registration across installer processes."""
    state_root = state_root.expanduser().absolute()
    if state_root.is_symlink(): raise ValueError(f"installer state directory is a symlink: {state_root}")
    state_root.mkdir(parents=True, exist_ok=True, mode=0o700)
    if state_root.is_symlink() or not state_root.is_dir():
        raise ValueError(f"installer state path is not a real directory: {state_root}")
    os.chmod(state_root, 0o700)
    lock_path = state_root / "installer.lock"
    flags = (os.O_CREAT | os.O_RDWR | getattr(os, "O_CLOEXEC", 0) |
             getattr(os, "O_NOFOLLOW", 0) | getattr(os, "O_NONBLOCK", 0))
    fd = os.open(lock_path, flags, 0o600)
    try:
        info = os.fstat(fd)
        if not stat.S_ISREG(info.st_mode) or info.st_nlink != 1:
            raise ValueError(f"installer lock is not a private regular file: {lock_path}")
        os.fchmod(fd, 0o600)
        try: fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError as ex:
            raise ValueError("another houseCARL installer operation is in progress; retry after it finishes") from ex
        yield
    finally:
        os.close(fd)


def register_hosts(version_dir: Path, prefix: Path, state_root: Path) -> None:
    """Register both hosts while preserving unrelated configuration and skills."""
    command = str((prefix / "current/server/housecarl-mcp").absolute())
    receipt_path = state_root / "host-registration.json"
    old_receipt = {}
    if receipt_path.exists():
        old_receipt = json.loads(receipt_path.read_text(encoding="utf-8"))
        if not isinstance(old_receipt, dict): raise ValueError("host registration receipt is malformed")
    skills = {
        Path.home() / ".claude/skills": version_dir / "claude/skills",
        Path.home() / ".agents/skills": version_dir / "codex/skills",
    }
    raw_skills = old_receipt.get("skills", {})
    if not isinstance(raw_skills, dict): raise ValueError("host registration skill receipt is malformed")
    installed_skills = dict(raw_skills)
    claude = Path.home() / ".claude.json"
    for unsafe in (claude, Path.home() / ".claude", Path.home() / ".codex", Path.home() / ".agents", Path.home() / ".codex/config.toml"):
        if unsafe.is_symlink(): raise ValueError(f"refusing symlinked host configuration path: {unsafe}")
    claude_data = {}
    if claude.exists():
        if not claude.is_file(): raise ValueError("~/.claude.json is not a regular file")
        claude_data = json.loads(claude.read_text(encoding="utf-8"))
        if not isinstance(claude_data, dict): raise ValueError("~/.claude.json root must be an object")
    servers = claude_data.setdefault("mcpServers", {})
    if not isinstance(servers, dict): raise ValueError("~/.claude.json mcpServers must be an object")
    prior_command = old_receipt.get("claude_command")
    if "housecarl" in servers and (not isinstance(servers["housecarl"], dict)
            or servers["housecarl"].get("command") != prior_command):
        raise ValueError("refusing to replace an unowned Claude housecarl registration")
    entry = servers.get("housecarl", {"type": "stdio", "args": []})
    entry["type"] = "stdio"
    entry["command"] = command
    entry.setdefault("args", [])
    servers["housecarl"] = entry

    codex = Path.home() / ".codex/config.toml"
    if codex.exists() and not codex.is_file(): raise ValueError("Codex config.toml is not a regular file")
    codex_text = codex.read_text(encoding="utf-8") if codex.exists() else ""
    codex_text = splice_codex_config(codex_text, command, old_receipt.get("codex_command"))

    staged: list[tuple[Path, Path, Path | None, str]] = []
    for host_root, source_root in skills.items():
        if not source_root.is_dir(): continue
        if host_root.is_symlink(): raise ValueError(f"refusing symlinked host skills root: {host_root}")
        host_root.mkdir(parents=True, exist_ok=True)
        for source in sorted(p for p in source_root.iterdir() if p.is_dir()):
            target = host_root / source.name
            key = str(target)
            expected = installed_skills.get(key)
            if target.exists() and (target.is_symlink() or expected is None or tree_digest(target) != expected):
                raise ValueError(f"refusing to replace unowned host skill directory: {target}")
            stage = host_root / ("." + source.name + ".housecarl-stage-" + secrets.token_hex(8))
            backup = host_root / ("." + source.name + ".housecarl-backup-" + secrets.token_hex(8)) if target.exists() else None
            shutil.copytree(source, stage)
            installed_skills[key] = tree_digest(stage)
            staged.append((target, stage, backup, key))

    prior_claude = claude.read_text(encoding="utf-8") if claude.exists() else None
    prior_codex = codex.read_text(encoding="utf-8") if codex.exists() else None
    receipt_path.parent.mkdir(parents=True, exist_ok=True)
    claude_changed = codex_changed = False
    published: list[tuple[Path, Path, Path | None]] = []
    created_backups: list[Path] = []
    try:
        for target, stage, backup, _ in staged:
            if backup is not None: os.replace(target, backup)
            os.replace(stage, target)
            published.append((target, stage, backup))
        if claude.exists():
            backup = claude.with_name(claude.name + ".houseCARL.bak")
            if backup.is_symlink(): raise ValueError(f"refusing symlinked host backup: {backup}")
            if not backup.exists(): shutil.copy2(claude, backup); created_backups.append(backup)
        atomic_text(claude, json.dumps(claude_data, ensure_ascii=False, indent=2) + "\n")
        claude_changed = True
        if codex.exists():
            backup = codex.with_name(codex.name + ".houseCARL.bak")
            if backup.is_symlink(): raise ValueError(f"refusing symlinked host backup: {backup}")
            if not backup.exists(): shutil.copy2(codex, backup); created_backups.append(backup)
        atomic_text(codex, codex_text)
        codex_changed = True
        atomic_text(receipt_path, json.dumps({"claude_command": command, "codex_command": command,
            "skills": installed_skills}, sort_keys=True, indent=2) + "\n")
    except Exception:
        if codex_changed:
            if prior_codex is None: codex.unlink(missing_ok=True)
            else: atomic_text(codex, prior_codex)
        if claude_changed:
            if prior_claude is None: claude.unlink(missing_ok=True)
            else: atomic_text(claude, prior_claude)
        for target, stage, backup in reversed(published):
            if target.exists(): shutil.rmtree(target)
            if backup is not None and backup.exists(): os.replace(backup, target)
        for _, stage, _, _ in staged:
            if stage.exists(): shutil.rmtree(stage)
        for backup in created_backups: backup.unlink(missing_ok=True)
        raise
    else:
        for _, stage, backup, _ in staged:
            if stage.exists(): shutil.rmtree(stage)
            if backup is not None and backup.exists(): shutil.rmtree(backup)


def tree_digest(path: Path) -> str:
    digest = hashlib.sha256()
    items = sorted(path.rglob("*"))
    for item in items:
        if item.is_symlink(): raise ValueError(f"host skill contains a symlink: {item}")
    for item in (p for p in items if p.is_file()):
        digest.update(item.relative_to(path).as_posix().encode()); digest.update(b"\0")
        with item.open("rb") as stream:
            for block in iter(lambda: stream.read(1024 * 1024), b""): digest.update(block)
    return digest.hexdigest()


def splice_codex_config(text: str, command: str, prior_command: str | None) -> str:
    encoded = json.dumps(command, ensure_ascii=False)
    lines = text.replace("\r\n", "\n").split("\n") if text else []
    begin = None
    end = len(lines)
    for i, line in enumerate(lines):
        if line.strip() == "[mcp_servers.housecarl]":
            begin = i
            end = next((j for j in range(i + 1, len(lines)) if lines[j].lstrip().startswith("[")), len(lines))
            break
    if begin is not None:
        expected = "command = " + json.dumps(prior_command, ensure_ascii=False) if prior_command is not None else None
        block = lines[begin:end]
        command_lines = [i for i, line in enumerate(block) if re.match(r"^\s*command\s*=", line)]
        if expected is None or len(command_lines) != 1 or block[command_lines[0]].strip() != expected:
            raise ValueError("refusing to replace an unowned Codex housecarl registration")
        lines[begin + command_lines[0]] = "command = " + encoded
        return "\n".join(lines)
    if lines and lines[-1] != "": lines.append("")
    lines.extend(["# houseCARL MCP server (managed by houseCARL-Amethyst)",
                  "[mcp_servers.housecarl]", "command = " + encoded, ""])
    return "\n".join(lines)


def unregister_hosts(state_root: Path) -> None:
    receipt_path = state_root / "host-registration.json"
    if not receipt_path.is_file(): return
    receipt = json.loads(receipt_path.read_text(encoding="utf-8"))
    if not isinstance(receipt, dict): raise ValueError("host registration receipt is malformed")
    claude = Path.home() / ".claude.json"
    codex = Path.home() / ".codex/config.toml"
    for unsafe in (claude, codex, Path.home() / ".claude", Path.home() / ".claude/skills",
                   Path.home() / ".codex", Path.home() / ".agents", Path.home() / ".agents/skills"):
        if unsafe.is_symlink(): raise ValueError(f"refusing symlinked host configuration path: {unsafe}")
    skills = receipt.get("skills", {})
    allowed_roots = {Path.home() / ".claude/skills", Path.home() / ".agents/skills"}
    if not isinstance(skills, dict): raise ValueError("host registration skill receipt is malformed")
    for raw_path in skills:
        path = Path(raw_path)
        if path.parent not in allowed_roots or path.name in ("", ".", ".."):
            raise ValueError(f"host registration receipt names an unowned skill path: {path}")
    if claude.is_file():
        data = json.loads(claude.read_text(encoding="utf-8"))
        if not isinstance(data, dict): raise ValueError("~/.claude.json root must be an object")
        servers = data.get("mcpServers", {})
        if isinstance(servers, dict):
            entry = servers.get("housecarl")
            if (isinstance(entry, dict) and entry.get("command") == receipt.get("claude_command")
                    and set(entry) == {"type", "command", "args"} and entry.get("type") == "stdio" and entry.get("args") == []):
                del servers["housecarl"]
                if not servers: data.pop("mcpServers", None)
                atomic_text(claude, json.dumps(data, ensure_ascii=False, indent=2) + "\n")
    if codex.is_file():
        text = codex.read_text(encoding="utf-8")
        stripped = remove_codex_config(text, receipt.get("codex_command"))
        if stripped != text: atomic_text(codex, stripped)
    if isinstance(skills, dict):
        for raw_path, expected in skills.items():
            path = Path(raw_path)
            if path.is_symlink(): raise ValueError(f"refusing to remove symlinked host skill: {path}")
            if path.is_dir() and tree_digest(path) == expected: shutil.rmtree(path)
    receipt_path.unlink()


def remove_codex_config(text: str, command: str | None) -> str:
    if command is None: return text
    lines = text.replace("\r\n", "\n").split("\n")
    for i, line in enumerate(lines):
        if line.strip() != "[mcp_servers.housecarl]": continue
        end = next((j for j in range(i + 1, len(lines)) if lines[j].lstrip().startswith("[")), len(lines))
        expected = "command = " + json.dumps(command, ensure_ascii=False)
        command_lines = [j for j in range(i + 1, end) if re.match(r"^\s*command\s*=", lines[j])]
        if len(command_lines) != 1 or lines[command_lines[0]].strip() != expected: return text
        del lines[command_lines[0]]
        remaining = [line for line in lines[i + 1:end - 1] if line.strip() and not line.lstrip().startswith("#")]
        if not remaining:
            del lines[i]
            if i > 0 and lines[i - 1].strip() == "# houseCARL MCP server (managed by houseCARL-Amethyst)": del lines[i - 1]
        return "\n".join(lines)
    return text


def extract_verified(archive: Path, manifest: dict, stage: Path) -> None:
    with tarfile.open(archive, "r:gz") as tar:
        for entry in manifest["files"]:
            rel = safe_rel(entry["path"])
            item = tar.getmember(rel)
            target = stage.joinpath(*PurePosixPath(rel).parts)
            target.parent.mkdir(parents=True, exist_ok=True)
            stream = tar.extractfile(item)
            if stream is None:
                raise ValueError(f"cannot extract {rel}")
            digest = hashlib.sha256()
            size = 0
            with target.open("xb") as output:
                for block in iter(lambda: stream.read(1024 * 1024), b""):
                    digest.update(block); size += len(block); output.write(block)
            if size != entry["size"] or digest.hexdigest() != entry["sha256"].lower():
                raise ValueError(f"package changed during extraction or payload hash mismatched: {rel}")
            target.chmod(entry["mode"])
    (stage / "manifest.json").write_text(json.dumps(manifest, sort_keys=True, indent=2) + "\n", encoding="utf-8")
    (stage / OWNED).write_text(json.dumps({"version": manifest["version"],
        "manifest_sha256": hashlib.sha256(json.dumps(manifest, sort_keys=True).encode()).hexdigest(),
        "transaction_journal_schema": manifest["transaction_journal_schema"]}, indent=2) + "\n", encoding="utf-8")


def validate_installed_version(path: Path) -> dict:
    manifest_path, owned_path = path / "manifest.json", path / OWNED
    if manifest_path.is_symlink() or owned_path.is_symlink() or not manifest_path.is_file() or not owned_path.is_file():
        raise ValueError(f"version directory has no safe package ownership manifest: {path}")
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    owned = json.loads(owned_path.read_text(encoding="utf-8"))
    if not isinstance(manifest, dict) or not isinstance(owned, dict):
        raise ValueError(f"version directory has a malformed ownership manifest: {path}")
    digest = hashlib.sha256(json.dumps(manifest, sort_keys=True).encode()).hexdigest()
    if manifest.get("version") != path.name or owned.get("version") != path.name or owned.get("manifest_sha256") != digest:
        raise ValueError(f"installed package ownership receipt does not match {path}")
    entries = manifest.get("files")
    if not isinstance(entries, list):
        raise ValueError(f"installed package manifest has no file list: {path}")
    expected_files = {"manifest.json", OWNED}
    expected_dirs: set[str] = set()
    for entry in entries:
        if not isinstance(entry, dict):
            raise ValueError(f"installed package has a malformed file entry: {path}")
        rel = safe_rel(entry.get("path", ""))
        if rel in expected_files or rel == "manifest.json":
            raise ValueError(f"installed package manifest has a duplicate or reserved path: {rel}")
        expected_files.add(rel)
        parent = PurePosixPath(rel).parent
        while parent != PurePosixPath("."):
            expected_dirs.add(parent.as_posix())
            parent = parent.parent
        target = path.joinpath(*PurePosixPath(rel).parts)
        if (target.is_symlink() or not target.is_file()
                or target.stat().st_size != entry.get("size")
                or hash_file(target) != entry.get("sha256")):
            raise ValueError(f"installed package file failed integrity validation: {target}")
    actual_files: set[str] = set()
    actual_dirs: set[str] = set()
    for root, dirnames, filenames in os.walk(path, topdown=True, followlinks=False):
        root_path = Path(root)
        for name in list(dirnames):
            candidate = root_path / name
            rel = candidate.relative_to(path).as_posix()
            if candidate.is_symlink() or not candidate.is_dir():
                raise ValueError(f"installed package contains an unsafe directory: {candidate}")
            actual_dirs.add(rel)
        for name in filenames:
            candidate = root_path / name
            rel = candidate.relative_to(path).as_posix()
            if candidate.is_symlink() or not stat.S_ISREG(candidate.lstat().st_mode):
                raise ValueError(f"installed package contains an unsafe file: {candidate}")
            actual_files.add(rel)
    if actual_files != expected_files or actual_dirs != expected_dirs:
        extra_files = sorted(actual_files - expected_files)
        missing_files = sorted(expected_files - actual_files)
        extra_dirs = sorted(actual_dirs - expected_dirs)
        missing_dirs = sorted(expected_dirs - actual_dirs)
        raise ValueError(
            f"installed package contains unowned or missing paths: {path}; "
            f"extra files={extra_files}, missing files={missing_files}, "
            f"extra directories={extra_dirs}, missing directories={missing_dirs}"
        )
    return manifest


def validate_activation(prefix: Path, link: Path, versions: Path, state_root: Path) -> Path | None:
    if not link.is_symlink():
        if link.exists(): raise ValueError(f"refusing to replace non-symlink activation path: {link}")
        return None
    target = (prefix / os.readlink(link)).resolve()
    if not target.is_dir() or not target.is_relative_to(versions.resolve()):
        raise ValueError(f"activation {link.name} points outside the owned versions directory")
    manifest = validate_installed_version(target)
    check_journal_compatibility(manifest, state_root)
    return target


def running_server_processes(prefix: Path, proc_root: Path = Path("/proc")) -> list[str]:
    versions = (prefix / "versions").resolve()
    try:
        entries = list(proc_root.iterdir())
    except OSError as ex:
        raise ValueError(f"cannot inspect process table {proc_root}; refusing to replace server: {ex}") from ex
    found = []
    for entry in entries:
        if not entry.name.isdigit(): continue
        try:
            executable = (entry / "exe").resolve(strict=True)
        except (OSError, RuntimeError):
            continue
        if executable.name == "housecarl-mcp" and executable.is_relative_to(versions):
            found.append(entry.name)
    return found


def refuse_running_server(prefix: Path) -> None:
    pids = running_server_processes(prefix)
    if pids:
        raise ValueError("houseCARL-Amethyst server is running (PID " + ", ".join(pids) + "); stop the host before switching versions")


def _install_unlocked(archive: Path, prefix: Path, state_root: Path) -> None:
    if archive.is_symlink() or not archive.is_file(): raise ValueError("package archive must be a regular non-symlink file")
    archive_hash_before = hash_file(archive)
    manifest, _ = inspect_archive(archive)
    check_journal_compatibility(manifest, state_root)
    prefix = prefix.expanduser().absolute()
    if prefix.is_symlink(): raise ValueError(f"installation prefix is a symlink: {prefix}")
    if prefix == Path("/") or prefix == Path.home():
        raise ValueError("choose a dedicated installation directory, not / or the home directory")
    versions = prefix / "versions"
    if versions.is_symlink(): raise ValueError(f"versions directory is a symlink: {versions}")
    refuse_running_server(prefix)
    previous_preflight = prefix / "previous"
    current_preflight = prefix / "current"
    validate_activation(prefix, current_preflight, versions, state_root)
    validate_activation(prefix, previous_preflight, versions, state_root)
    versions.mkdir(parents=True, exist_ok=True)
    version_dir = versions / manifest["version"]
    if version_dir.exists():
        owner = version_dir / OWNED
        if not owner.is_file():
            raise ValueError(f"refusing to overwrite unowned version directory: {version_dir}")
        raise ValueError(f"version {manifest['version']} is already installed; choose a new package version")
    stage = Path(tempfile.mkdtemp(prefix=".stage-", dir=versions))
    version_created = False
    current = prefix / "current"
    previous = prefix / "previous"
    original_current = os.readlink(current) if current.is_symlink() else None
    original_previous = os.readlink(previous) if previous.is_symlink() else None
    try:
        extract_verified(archive, manifest, stage)
        if hash_file(archive) != archive_hash_before:
            raise ValueError("package archive changed during validation/extraction")
        os.replace(stage, version_dir)
        version_created = True
        if current.is_symlink():
            old = os.readlink(current)
            old_resolved = (prefix / old).resolve()
            if not old_resolved.is_dir() or not old_resolved.is_relative_to(versions.resolve()):
                raise ValueError("current activation points outside the owned versions directory")
            if previous.is_symlink():
                previous_target = (prefix / os.readlink(previous)).resolve()
                if not previous_target.is_dir() or not previous_target.is_relative_to(versions.resolve()):
                    raise ValueError("previous activation points outside the owned versions directory")
            atomic_link(previous, os.path.relpath(old_resolved, prefix))
        elif current.exists():
            raise ValueError(f"refusing to replace non-symlink activation path: {current}")
        atomic_link(current, os.path.relpath(version_dir, prefix))
        register_hosts(version_dir, prefix, state_root)
        print(f"Installed {manifest['version']} at {version_dir}; active server: {current / 'server/housecarl-mcp'}")
        print("Claude Code and Codex registrations point to the versioned current activation; user config and transaction state remain outside the install tree.")
    except Exception:
        if stage.exists(): shutil.rmtree(stage)
        if original_current is None:
            if current.is_symlink(): current.unlink()
        else:
            atomic_link(current, original_current)
        if original_previous is None:
            if previous.is_symlink(): previous.unlink()
        else:
            atomic_link(previous, original_previous)
        if version_created:
            if not current.is_symlink() or (prefix / os.readlink(current)).resolve() != version_dir.resolve():
                shutil.rmtree(version_dir)
        raise


def _rollback_unlocked(prefix: Path, state_root: Path) -> None:
    prefix = prefix.expanduser().absolute()
    if prefix.is_symlink(): raise ValueError(f"installation prefix is a symlink: {prefix}")
    refuse_running_server(prefix)
    versions_path = prefix / "versions"
    if versions_path.is_symlink() or not versions_path.is_dir(): raise ValueError("versions directory is missing or is a symlink")
    current, previous, versions = prefix / "current", prefix / "previous", versions_path.absolute()
    if not current.is_symlink() or not previous.is_symlink():
        raise ValueError("rollback requires both current and previous package activations")
    for link in (current, previous):
        target = (prefix / os.readlink(link)).resolve()
        if not target.is_dir() or not target.is_relative_to(versions):
            raise ValueError(f"activation {link.name} points outside the owned versions directory")
        owner = target / OWNED
        if not owner.is_file(): raise ValueError(f"activation target has no ownership receipt: {target}")
        manifest = validate_installed_version(target)
        check_journal_compatibility(manifest, state_root)
    cur = os.readlink(current); prev = os.readlink(previous)
    atomic_link(current, prev)
    atomic_link(previous, cur)
    try:
        register_hosts(current.resolve(), prefix, state_root)
    except Exception:
        atomic_link(current, cur)
        atomic_link(previous, prev)
        raise
    print(f"Rolled back active package: {current} -> {os.readlink(current)}")


def _uninstall_unlocked(prefix: Path, state_root: Path) -> None:
    prefix = prefix.expanduser().absolute()
    if prefix.is_symlink(): raise ValueError(f"installation prefix is a symlink: {prefix}")
    refuse_running_server(prefix)
    versions_path = prefix / "versions"
    if versions_path.is_symlink(): raise ValueError("versions path is a symlink")
    if versions_path.exists() and not versions_path.is_dir(): raise ValueError("versions path is not a directory")
    versions = versions_path.absolute()
    owned_versions = []
    if versions_path.exists():
        for entry in versions_path.iterdir():
            if entry.is_symlink() or not entry.is_dir() or not (entry / OWNED).is_file():
                raise ValueError(f"refusing to uninstall with an unowned or unsafe version entry: {entry}")
            owned_versions.append(entry)
    for version in owned_versions:
        manifest = validate_installed_version(version)
        check_journal_compatibility(manifest, state_root)
    known_versions = {path.resolve() for path in owned_versions}
    for link in (prefix / "current", prefix / "previous"):
        if link.is_symlink():
            target = (prefix / os.readlink(link)).resolve()
            if target not in known_versions: raise ValueError(f"refusing to remove activation that is not a validated owned version: {link}")
        elif link.exists(): raise ValueError(f"refusing to remove non-symlink activation: {link}")
    unregister_hosts(state_root)
    for link in (prefix / "current", prefix / "previous"):
        if link.is_symlink(): link.unlink()
    if (prefix / "versions").is_dir():
        for version in (prefix / "versions").iterdir():
            if version.is_symlink(): raise ValueError(f"refusing to remove a symlink in versions: {version}")
            if version.is_dir() and (version / OWNED).is_file():
                shutil.rmtree(version)
    print(f"Removed owned program versions under {prefix}. User configuration and transaction recovery data were retained.")


def install(archive: Path, prefix: Path, state_root: Path) -> None:
    with installer_lock(state_root): _install_unlocked(archive, prefix, state_root)


def rollback(prefix: Path, state_root: Path) -> None:
    with installer_lock(state_root): _rollback_unlocked(prefix, state_root)


def uninstall(prefix: Path, state_root: Path) -> None:
    with installer_lock(state_root): _uninstall_unlocked(prefix, state_root)


def main() -> int:
    if sys.version_info < (3, 12):
        print("install-linux: Python 3.12 or newer is required; found " + sys.version.split()[0], file=sys.stderr)
        return 2
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("install", "rollback", "uninstall"))
    parser.add_argument("--archive", type=Path)
    parser.add_argument("--prefix", type=Path, default=Path.home() / ".local/opt/housecarl-amethyst")
    parser.add_argument("--state-root", type=Path, default=Path(os.environ.get("XDG_STATE_HOME", Path.home() / ".local/state")) / "housecarl-amethyst")
    args = parser.parse_args()
    try:
        if args.action == "install":
            if args.archive is None: parser.error("install requires --archive")
            install(args.archive, args.prefix, args.state_root)
        elif args.action == "rollback": rollback(args.prefix, args.state_root)
        else: uninstall(args.prefix, args.state_root)
    except (OSError, ValueError, tarfile.TarError, json.JSONDecodeError) as ex:
        print(f"install-linux: {ex}", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
