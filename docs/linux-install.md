# Install the Linux 2.1.0 preview

Version [2.1.0 is a prerelease (Linux preview)](https://github.com/ni1by2/houseCARL-Amethyst/releases/tag/v2.1.0), targeting Ubuntu 24.04 x64. Its Amethyst snapshot helper requires Python 3.12 or newer. The [2.1.0 verification record](testing/upstream-210-integration-2026-10-20.md) covers the updated engine, transaction regressions and package checks. Installation, isolated Claude Code/Codex registration and the Proton CachyOS utility round trip were checked for [2.0.5](testing/upstream-205-integration-2026-10-15.md); those checks were not repeated for 2.1.0. The installer and pinned native utilities are unchanged.

Download these three assets into the same directory:

The published 2.1.0 archive supports filegraph schema 9/rules 13 only. Amethyst
2.6.0-beta.7 uses schema 10/rules 14 and requires the 2.1.1 compatibility candidate,
which is not yet a published release. Reinstalling 2.1.0 cannot resolve that
contract mismatch; see the [source build guide](linux-development.md).

- [housecarl-amethyst-linux-x64.tar.gz](https://github.com/ni1by2/houseCARL-Amethyst/releases/download/v2.1.0/housecarl-amethyst-linux-x64.tar.gz)
- [housecarl-amethyst-linux-x64.tar.gz.sha256](https://github.com/ni1by2/houseCARL-Amethyst/releases/download/v2.1.0/housecarl-amethyst-linux-x64.tar.gz.sha256)
- [install-linux.py](https://github.com/ni1by2/houseCARL-Amethyst/releases/download/v2.1.0/install-linux.py)

## Quick start

Install the [runtime prerequisites](#runtime-requirements) first. From the directory containing all three downloads, verify the archive and then install it:

```sh
sha256sum -c housecarl-amethyst-linux-x64.tar.gz.sha256 &&
  python3 install-linux.py install --archive housecarl-amethyst-linux-x64.tar.gz
```

Continue with installation only if the checksum reports `OK`. The `install` action and `--archive` argument are required. Running `python3 install-linux.py` alone exits with the missing-action error; it does not install anything. Use your normal user account for the installer. Restart Claude Code or Codex afterward and [select your Amethyst connection](#select-an-amethyst-connection).

Use the checksum asset from the same release as the archive. The release notes also record its SHA-256. Do not mix the 2.0.5 archive or installer with the 2.1.0 downloads.

## Runtime requirements

The package includes .NET itself; it also needs the operating system's runtime libraries. On Ubuntu 24.04, install Python and those libraries with:

```sh
sudo apt-get install python3 ca-certificates libc6 libgcc-s1 libgssapi-krb5-2 libicu74 libssl3t64 libstdc++6 zlib1g
```

ICU is needed even for MCP startup. A build machine may already have it through Clang, while a minimal Ubuntu installation does not. The .NET SDK and C++ build tools are unnecessary for running the package.

## Install, update and remove

The offline installer keeps program versions under `~/.local/opt/housecarl-amethyst/versions`, activating them through `current` and `previous` links. It validates the archive manifest and every payload hash before activation, refuses unknown or pending transaction journals, and retains user configuration and transaction state outside the program directory. `rollback` switches to the previous owned version. `uninstall` removes only validated owned versions and host registration; modified or unrelated host settings remain.

Install with:

```sh
python3 install-linux.py install --archive housecarl-amethyst-linux-x64.tar.gz
```

The installer registers the server for Claude Code in `~/.claude.json` and Codex in `~/.codex/config.toml`, preserving unrelated entries. It installs the bundled skill directories under each host's skills root, refuses to overwrite unowned skill folders, and records hashes outside the program tree so uninstall removes only unchanged skill payloads. Existing host configuration files are backed up beside the originals with a `.houseCARL.bak` suffix. Restart the host after installation so it reloads the MCP server and skill inventory.

Use `python3 install-linux.py rollback` to switch back or `python3 install-linux.py uninstall` to remove the program. Both actions refuse to switch or remove files while a transaction journal is pending, corrupt, or newer than the package understands. The installer does not remove Amethyst data, houseCARL transaction recovery data, unrelated settings, or modified skill directories.

To update from an earlier package, run the new release's installer with its archive using the same `install` command. The previous activation is retained for rollback. Installing a version that is already present refuses to overwrite it.

For a 2.0.5 installation, download all three 2.1.0 assets into a new directory and run the Quick start commands above. Your selected Amethyst connection and transaction state remain outside the version directory and are retained. Restart Claude Code or Codex afterward; an already running MCP process continues to use its old program until restarted. Updating the server does not refresh or deploy your Amethyst profile.

If installation reports an unfinished transaction, preserve its journal and inspect that exact ID with `housecarl_staging_status` in the current compatible server. Use the supported recovery action for its recorded state before retrying installation. If no compatible server is available, stop and request recovery help. Do not delete journals or select a different state root to bypass the check. Older development test runs could leave journals referencing deleted temporary fixtures; establish their origin and preserve a backup before quarantining any such records.

## Select an Amethyst connection

Installation registers the MCP server; it does not select or change an Amethyst profile. After restarting Claude Code or Codex, select your existing schema-v1 connection manifest with `housecarl_set_amethyst_connection`, passing its absolute path as `manifest_path` and explicitly setting `filegraph_mode="saved-snapshot"`.

If you do not have a manifest, save a separate JSON file using the following structure and replace every placeholder with an existing absolute native path:

```json
{
  "schemaVersion": 1,
  "manager": "amethyst",
  "gameId": "skyrim_se",
  "profileRoot": "/absolute/path/to/Amethyst/staging-root",
  "gameConfigDir": "/absolute/path/to/AmethystModManager/games/skyrim_se",
  "pathsFile": "/absolute/path/to/AmethystModManager/games/skyrim_se/paths.json",
  "deployStateFile": "/absolute/path/to/AmethystModManager/games/skyrim_se/deploy_state.json"
}
```

Use Amethyst's existing configuration paths. `profileRoot` is the staging root containing `mods` and `profiles`, not an individual profile directory; it must match a nonempty `staging_path` in `paths.json`. Both JSON configuration files must reside directly inside `gameConfigDir`. Do not edit those manager files to fit the example. The server validates the selected saved filegraph and refuses incompatible or incomplete inputs. Automatic connection discovery is also available through the [source-only setup command](linux-development.md#create-a-connection-manifest-from-source), which requires the .NET SDK.

Saved-snapshot responses identify the profile and generation used and warn that changes since Amethyst's last Refresh may be missing. Writes stay in staging until you review, Refresh, enable and deploy them through Amethyst. A later deployment check verifies which file wins; a successful staging write alone does not establish game visibility.

## Release limits

Windows utilities such as the Creation Kit compiler and BSArch require Skyrim SE's explicit Steam compatibility-tool selection and the matching Amethyst prefix. Runner discovery includes Steam library installations, the user's `compatibilitytools.d`, and `/usr/share/steam/compatibilitytools.d` and `/usr/local/share/steam/compatibilitytools.d` for distro packages such as Proton CachyOS. These directories can contain a tool subdirectory or a root-level `.vdf` manifest with a relative or absolute installation path. Relative paths must stay inside the manifest's directory after resolving symlinks. Multiple matching runner copies refuse instead of guessing. Discovery reads configuration only; it does not select a tool in Steam, create a prefix or reconfigure an existing prefix.

The package build fetches the pinned Caprica and Champollion source commits, builds them with the pinned vcpkg dependency baseline, smoke-tests a synthetic compile/decompile round trip, and stages source archives and license notices. The [2.0.5 verification record](testing/upstream-205-integration-2026-10-15.md) separates earlier clean Ubuntu install/upgrade/rollback checks from final-source CI, package verification and isolated Arch-derived installation/Proton acceptance.

The selected-Proton check used real BSArch with Proton CachyOS, a copied prefix and synthetic inputs. It verified pack/list/extract and byte equality without touching the live profile. It does not establish Creation Kit compiler compatibility with that runner. Flatpak/custom Steam runner layouts are unsupported. Windows execution, the actual manager GUI workflow, live-profile deployment and gameplay were not tested. Package verification does not prove which outputs the game currently loads.
