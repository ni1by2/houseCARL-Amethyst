#!/usr/bin/env python3
"""Validate and start the packaged Linux server without registering hosts or opening a profile."""
from __future__ import annotations

import argparse
import importlib.util
import json
import os
from pathlib import Path
import queue
import subprocess
import sys
import tarfile
import tempfile
import threading


def check(archive: Path, installed_prefix: Path | None = None) -> None:
    installer_path = Path(__file__).resolve().parents[1] / "install-linux.py"
    spec = importlib.util.spec_from_file_location("linux_installer_probe", installer_path)
    installer = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(installer)
    manifest, _ = installer.inspect_archive(archive)

    with tempfile.TemporaryDirectory(prefix="housecarl-package-check-") as raw:
        root = Path(raw)
        if installed_prefix is None:
            payload = root / "payload"
            with tarfile.open(archive, "r:gz") as tar:
                tar.extractall(payload, filter="data")
        else:
            payload = (installed_prefix / "current").resolve(strict=True)
            if installer.validate_installed_version(payload) != manifest:
                raise RuntimeError("installed payload does not match the candidate archive manifest")
        schemas = manifest.get("native_filegraph_schemas", [manifest["native_filegraph_schema"]])
        if schemas not in ([9], [9, 10]) or any(type(schema) is not int for schema in schemas):
            raise ValueError("unsupported packaged Amethyst native schema list")
        for schema in schemas:
            filename = "amethyst_filegraph.abi3.so" if schema == 9 else f"amethyst_filegraph_schema{schema}.abi3.so"
            native_check = subprocess.run([
                sys.executable, "-I", "-c",
                "import importlib.util, sys; "
                "spec = importlib.util.spec_from_file_location('amethyst_filegraph', sys.argv[1]); "
                "native = importlib.util.module_from_spec(spec); spec.loader.exec_module(native); "
                "assert native.api_version() == int(sys.argv[2]), 'native filegraph API mismatch'; "
                "assert native.schema_version() == int(sys.argv[3]), 'native filegraph schema mismatch'",
                str(payload / "server" / filename),
                str(manifest["native_filegraph_api"]), str(schema),
            ], capture_output=True, text=True, timeout=30)
            if native_check.returncode:
                raise RuntimeError(f"packaged Amethyst schema-{schema} module failed to load: {native_check.stderr}")
        env = dict(os.environ, HOUSECARL_DATA_DIR=str(root / "user-data"))
        with tempfile.TemporaryFile(mode="w+t", encoding="utf-8") as errors:
            proc = subprocess.Popen(
                [str(payload / "server/housecarl-mcp")], cwd=payload,
                env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                stderr=errors, text=True, encoding="utf-8",
            )
            lines: queue.Queue[str | None] = queue.Queue()

            def pump() -> None:
                try:
                    for line in proc.stdout:
                        lines.put(line)
                finally:
                    lines.put(None)

            reader = threading.Thread(target=pump, daemon=True)
            reader.start()

            def send(message: dict) -> None:
                proc.stdin.write(json.dumps(message) + "\n")
                proc.stdin.flush()

            def rpc(ident: int, method: str, params: dict) -> dict:
                send({"jsonrpc": "2.0", "id": ident, "method": method, "params": params})
                while True:
                    try:
                        line = lines.get(timeout=60)
                    except queue.Empty as ex:
                        raise RuntimeError(f"packaged server timed out on {method}") from ex
                    if line is None:
                        raise RuntimeError(f"packaged server closed stdout on {method}")
                    message = json.loads(line)
                    if message.get("id") == ident:
                        if "error" in message:
                            raise RuntimeError(f"{method} failed: {message['error']}")
                        return message["result"]

            try:
                initialized = rpc(1, "initialize", {
                    "protocolVersion": "2025-06-18", "capabilities": {},
                    "clientInfo": {"name": "linux-package-check", "version": "1"},
                })
                version = initialized["serverInfo"]["version"]
                if version != manifest["version"]:
                    raise RuntimeError(f"server version {version!r} differs from package {manifest['version']!r}")
                send({"jsonrpc": "2.0", "method": "notifications/initialized"})
                tools: set[str] = set()
                cursor = None
                ident = 2
                while True:
                    result = rpc(ident, "tools/list", {} if cursor is None else {"cursor": cursor})
                    tools.update(tool["name"] for tool in result["tools"])
                    cursor = result.get("nextCursor")
                    ident += 1
                    if not cursor:
                        break
                required = {"housecarl_records", "housecarl_apply", "housecarl_set_amethyst_connection"}
                if missing := required - tools:
                    raise RuntimeError(f"packaged server omitted tools: {sorted(missing)}")
                print(f"Packaged Amethyst modules: API {manifest['native_filegraph_api']}, schemas {schemas} passed.")
                print(f"Packaged server {version}: MCP initialize and tools/list passed ({len(tools)} tools).")
            except Exception:
                errors.flush()
                errors.seek(0)
                diagnostic = errors.read()
                if diagnostic:
                    print(diagnostic[-12000:])
                raise
            finally:
                try:
                    proc.stdin.close()
                except BrokenPipeError:
                    pass  # The server may already have exited on a missing runtime dependency.
                try:
                    proc.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    proc.kill()
                    proc.wait(timeout=10)
                reader.join(timeout=5)
                proc.stdout.close()


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("archive", type=Path)
    parser.add_argument("--installed-prefix", type=Path,
                        help="check an installed current version instead of extracting the archive")
    args = parser.parse_args()
    check(args.archive.resolve(), args.installed_prefix)


if __name__ == "__main__":
    main()
