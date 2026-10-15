# Linux package candidate

The Linux x64 package is a preview candidate targeting Ubuntu 24.04 x64. Its Amethyst snapshot helper requires Python 3.12 or newer. Compatibility with the Arch-derived development environment still needs a separate acceptance run.

The offline installer keeps program versions under `~/.local/opt/housecarl-amethyst/versions`, activating them through `current` and `previous` links. It validates the archive manifest and every payload hash before activation, refuses unknown or pending transaction journals, and retains user configuration and transaction state outside the program directory. `rollback` switches to the previous owned version. `uninstall` removes only validated owned versions and host registration; modified or unrelated host settings remain.

Install with:

```sh
python3 scripts/install-linux.py install --archive housecarl-amethyst-linux-x64.tar.gz
```

The installer registers the server for Claude Code in `~/.claude.json` and Codex in `~/.codex/config.toml`, preserving unrelated entries. It installs the bundled skill directories under each host's skills root, refuses to overwrite unowned skill folders, and records hashes outside the program tree so uninstall removes only unchanged skill payloads. Existing host configuration files are backed up beside the originals with a `.houseCARL.bak` suffix. Restart the host after installation so it reloads the MCP server and skill inventory.

Use `python3 scripts/install-linux.py rollback` to switch back or `python3 scripts/install-linux.py uninstall` to remove the program. Both actions refuse to switch or remove files while a transaction journal is pending, corrupt, or newer than the package understands. The installer does not remove Amethyst data, houseCARL transaction recovery data, unrelated settings, or modified skill directories.

## Release limits

The package build fetches the pinned Caprica and Champollion source commits, builds them with the pinned vcpkg dependency baseline, smoke-tests a synthetic compile/decompile round trip, and stages source archives and license notices. The source build has been verified on the development host; a clean Ubuntu 24.04 build/install/upgrade run, a real selected-Proton tool run, a native Windows test run, and a separate Arch-derived package acceptance run remain outstanding. These acceptance runs are required before release.
