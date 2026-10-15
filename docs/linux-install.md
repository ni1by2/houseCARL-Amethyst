# Linux package candidate

The Linux x64 package is a preview candidate targeting Ubuntu 24.04 x64. Its Amethyst snapshot helper requires Python 3.12 or newer. Version 2.0.5 passed installation and startup checks in a fresh Ubuntu container and archive startup on the Arch-derived development host; full desktop-host acceptance remains separate.

The package includes .NET itself; it also needs the operating system's runtime libraries. On Ubuntu 24.04, install Python and those libraries with:

```sh
sudo apt-get install python3 ca-certificates libc6 libgcc-s1 libgssapi-krb5-2 libicu74 libssl3t64 libstdc++6 zlib1g
```

ICU is needed even for MCP startup. A build machine may already have it through Clang, while a minimal Ubuntu installation does not. The .NET SDK and C++ build tools are unnecessary for running the package.

The offline installer keeps program versions under `~/.local/opt/housecarl-amethyst/versions`, activating them through `current` and `previous` links. It validates the archive manifest and every payload hash before activation, refuses unknown or pending transaction journals, and retains user configuration and transaction state outside the program directory. `rollback` switches to the previous owned version. `uninstall` removes only validated owned versions and host registration; modified or unrelated host settings remain.

Install with:

```sh
python3 install-linux.py install --archive housecarl-amethyst-linux-x64.tar.gz
```

The installer registers the server for Claude Code in `~/.claude.json` and Codex in `~/.codex/config.toml`, preserving unrelated entries. It installs the bundled skill directories under each host's skills root, refuses to overwrite unowned skill folders, and records hashes outside the program tree so uninstall removes only unchanged skill payloads. Existing host configuration files are backed up beside the originals with a `.houseCARL.bak` suffix. Restart the host after installation so it reloads the MCP server and skill inventory.

Use `python3 install-linux.py rollback` to switch back or `python3 install-linux.py uninstall` to remove the program. Both actions refuse to switch or remove files while a transaction journal is pending, corrupt, or newer than the package understands. The installer does not remove Amethyst data, houseCARL transaction recovery data, unrelated settings, or modified skill directories.

## Release limits

Windows utilities such as the Creation Kit compiler and BSArch require Skyrim SE's explicit Steam compatibility-tool selection and the matching Amethyst prefix. Runner discovery includes Steam library installations, the user's `compatibilitytools.d`, and `/usr/share/steam/compatibilitytools.d` and `/usr/local/share/steam/compatibilitytools.d` for distro packages such as Proton CachyOS. These directories can contain a tool subdirectory or a root-level `.vdf` manifest with a relative or absolute installation path. Relative paths must stay inside the manifest's directory after resolving symlinks. Multiple matching runner copies refuse instead of guessing. Discovery reads configuration only; it does not select a tool in Steam, create a prefix or reconfigure an existing prefix.

The package build fetches the pinned Caprica and Champollion source commits, builds them with the pinned vcpkg dependency baseline, smoke-tests a synthetic compile/decompile round trip, and stages source archives and license notices. The [2.0.5 integration record in the source repository](https://github.com/ni1by2/houseCARL-Amethyst/blob/amethyst-main/docs/testing/upstream-205-integration-2026-10-15.md) records the Linux suite, Ubuntu build, clean installation, candidate upgrade/rollback and archive startup checks.

A real selected-Proton utility run and acceptance against the user's Amethyst installation remain outstanding. The Arch check covers the extracted server and native filegraph module; it does not establish desktop host registration or deployment behaviour. Windows execution is outside this Linux release's supported target. Package verification does not prove which outputs the game currently loads.
