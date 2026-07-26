# Troubleshooting

houseCARL-Amethyst fails loudly when Amethyst state is incomplete or
inconsistent. Do not work around these errors by pointing it at deployed
`Data`; refresh the manager state or repair the named input.

## The installer finds no usable Amethyst configuration

Open the Skyrim SE profile in Amethyst and confirm that its game configuration
contains `paths.json`, `deploy_state.json`, a profile root, and at least one
profile with `loadorder.txt`. Then rerun `--connect-amethyst`.

For Flatpak, the expected configuration root is beneath:

```text
$HOME/.var/app/io.github.Amethyst.ModManager/config/AmethystModManager
```

For native, AppImage, and AUR-style installs, it is beneath the applicable XDG
configuration root.

## More than one Amethyst configuration is usable

Rerun discovery with `--amethyst-config` and the absolute
`AmethystModManager` configuration directory. The installer refuses to guess
between installations.

## Deployment is active but Data_Core is missing

houseCARL will not treat merged deployed `Data` as vanilla. Restore or
redeploy the profile through Amethyst so `Data_Core` is present, then rerun
`housecarl_refresh`.

## filemap.txt or modindex.bin is missing or stale

Refresh Amethyst and rebuild the profile's filemap. Both files must belong to
the active profile's effective shared or profile-specific staging layout.
houseCARL does not scan staging or deployed `Data` to guess missing winners.

An unsupported `modindex.bin` version is a compatibility event. Update
houseCARL-Amethyst or Amethyst to a compatible pair; do not rewrite the binary
index manually.

## A staged source named by the index is missing

The manager index and staging contents disagree. Refresh Amethyst, rebuild the
filemap/index, and check whether the providing mod was moved or removed.
Linux path casing is preserved from `modindex.bin`.

## A new patch is not visible in game

Creating a staging mod is not deployment. In Amethyst, refresh, enable the new
mod, rebuild the filemap, and deploy. Then call `housecarl_refresh`.

## An in-place edit remains pending

Atomic replacement gives the staging plugin a new inode. An existing hardlink
in deployed `Data` can still point to the old inode until Amethyst redeploys.
Pending state clears only when:

- the same profile is active;
- deployment is active;
- filemap and deployment state are newer than the write; and
- the deployed file matches the new staging content or hardlink identity.

Never copy the file through deployed `Data` to clear the warning. Redeploy
through Amethyst.

## Codex or Claude Code does not show the server

Confirm that installation targeted the intended host, fully restart that host,
and verify that the registered executable still exists under the XDG data
root. Rerun the matching `--codex`, `--claude`, or `--both` install command if
the host configuration was replaced.

## Papyrus compilation or BSA repacking returns a deferred error

This is expected in native v1. The public tool names are reserved, but direct
execution is disabled until a structured Proton runner can preserve executable
path, prefix, arguments, environment, and working directory without shell
string construction.

Native record operations, BSA listing/extraction, PEX decompilation, NIF
operations, and Amethyst staging do not require Proton.

## Diagnostic log folders are unset

Configure existing native directories with:

```text
housecarl_set_tool_path(tool="papyrus_logs", path="/absolute/directory")
housecarl_set_tool_path(tool="crash_logs", path="/absolute/directory")
```

Executable keys such as `papyrus_compiler` and `bsarch` are intentionally
rejected.

## Reporting a bug

Include the houseCARL-Amethyst commit or release, Linux distribution, Amethyst
version and profile, deployment mode, Skyrim version, exact tool call, and full
error. Scrub usernames and local paths before posting public logs.
