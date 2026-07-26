# Installation and updates

houseCARL-Amethyst is a self-contained Linux x86_64 MCP server for Codex and
Claude Code. It requires Amethyst to have a usable Skyrim Special Edition
profile, but it does not require system .NET, Wine, or an Amethyst plugin.

## Prepare Amethyst

Before connecting houseCARL:

1. Open the intended Skyrim SE profile in Amethyst.
2. Refresh the profile and rebuild its filemap.
3. Confirm that the profile has `loadorder.txt`, `plugins.txt`, and
   `modlist.txt`.
4. If the profile is deployed, confirm that the game directory contains
   `Data_Core`. houseCARL refuses a deployed merged `Data` directory as a
   vanilla source.

Hardlink deployment is the v1 release target. Symlink and copy deployments
have synthetic smoke coverage but are not release gates.

## Install a release

Extract the complete release archive, enter its directory, and select one host:

```bash
./housecarl-amethyst-install --codex
./housecarl-amethyst-install --claude
./housecarl-amethyst-install --both
```

The installer verifies `SHA256SUMS` before changing anything. It installs the
shared server under:

```text
${XDG_DATA_HOME:-$HOME/.local/share}/housecarl-amethyst/server
```

Mutable connection, consent, and pending-redeployment state remains under:

```text
${XDG_CONFIG_HOME:-$HOME/.config}/housecarl-amethyst
```

Fully restart the selected host after installation.

## Create the Amethyst connection

Discover a usable native/AppImage/AUR-style or Flatpak Amethyst setup:

```bash
./housecarl-amethyst-install --connect-amethyst
```

If more than one usable configuration is found, choose one explicitly:

```bash
./housecarl-amethyst-install --connect-amethyst \
  --amethyst-config /absolute/path/to/AmethystModManager
```

The command validates Amethyst's Skyrim state and atomically writes:

```text
<profile root>/.housecarl-amethyst/connection.json
```

It never edits Amethyst's profile, deployment, or filemap files.

In Codex or Claude Code, call `housecarl_set_amethyst_connection` with the
printed absolute manifest path. Then call `housecarl_amethyst_status` and
confirm the active profile, staging mode, game path, vanilla Data path,
deployment mode, filemap, and mod-index paths.

## Normal patch workflow

houseCARL writes a new mod beneath the effective Amethyst staging directory.
After each new patch:

1. Refresh Amethyst.
2. Enable the generated mod.
3. Rebuild the filemap.
4. Deploy.
5. Call `housecarl_refresh`.

houseCARL never edits `modlist.txt`, `plugins.txt`, `loadorder.txt`, or
`filemap.txt`.

## Update

Quit Codex and Claude Code. Extract the newer complete release and run the same
host-selection command used for installation. The installer verifies the new
payload, keeps one prior server as rollback state, atomically activates the new
server, and preserves XDG configuration.

Restart the host and verify `housecarl_amethyst_status`.

## Roll back

Quit the hosts, then run from a complete extracted release:

```bash
./housecarl-amethyst-install --rollback --codex
./housecarl-amethyst-install --rollback --claude
./housecarl-amethyst-install --rollback --both
```

Rollback swaps the shared server back to the retained prior version. It does
not alter the Amethyst connection or pending write state.

## Uninstall

```bash
./housecarl-amethyst-install --uninstall --codex
./housecarl-amethyst-install --uninstall --claude
./housecarl-amethyst-install --uninstall --both
```

Uninstall removes the server, installed skills, rollback copy, and only the
houseCARL MCP registrations. It deliberately preserves the XDG configuration
directory.

## Build from source

```bash
dotnet restore
dotnet build housecarl.sln --configuration Release
./scripts/build-release.sh
```

The release archive and its SHA-256 checksum are written under `release/`.
