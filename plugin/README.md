# houseCARL-Amethyst

houseCARL-Amethyst gives Codex and Claude Code native Linux access to a
Skyrim Special Edition profile managed by Amethyst. It reads Amethyst's active
profile, plugin order, staging directories, `filemap.txt`, and `modindex.bin`
directly. Mod Organizer 2, Wine, and a houseCARL-specific Amethyst plugin are
not required.

The MCP server preserves upstream houseCARL's reflection-driven Mutagen record
coverage. It can inspect records and conflicts, author reviewable patches,
resolve loose and archived assets, inspect NIF and SKSE layers, analyze
SkyPatcher state, and use the bundled Skyrim authoring/reference skills.

## Requirements

- Linux x86_64.
- Amethyst Mod Manager with a Skyrim Special Edition profile.
- Codex, Claude Code, or both.

Hardlink deployment is the verified v1 target. Symlink and copy modes have
synthetic smoke coverage but are not release gates. Amethyst has no MO2-style
VFS deployment backend.

The release server is self-contained; system .NET is not required.

## Install

Use the installer in the release root:

```bash
./housecarl-amethyst-install --codex
./housecarl-amethyst-install --claude
./housecarl-amethyst-install --both
```

It verifies `SHA256SUMS`, installs one shared server under
`${XDG_DATA_HOME:-$HOME/.local/share}/housecarl-amethyst/server`, installs the
bundled skills, and registers the stdio MCP server. Mutable connection and
write-safety state remains under
`${XDG_CONFIG_HOME:-$HOME/.config}/housecarl-amethyst` across upgrades.

Create the manifest with
`./housecarl-amethyst-install --connect-amethyst`. After restarting the
selected host, pass the printed absolute path to
`housecarl_set_amethyst_connection`, then run `housecarl_amethyst_status`.

## Safe writes

Normal writes create a new staging mod:

```text
<effective mods directory>/houseCARL - <patch name>/
├── <patch name>.esp
└── meta.ini
```

Refresh Amethyst, enable the generated mod, rebuild the filemap, and deploy.
houseCARL never edits Amethyst's `modlist.txt`, `plugins.txt`,
`loadorder.txt`, or `filemap.txt`.

In-place editing additionally requires `in_place=true`, the per-plugin consent
handshake, and `confirm_amethyst_redeploy=true`. It writes staging only. With
hardlink deployment, atomic replacement gives staging a new inode while the
deployed pathname can retain the old inode until Amethyst redeploys; the server
tracks this state and will not claim game-visible success prematurely.

## Update, rollback, and uninstall

Run the same install command from a newer extracted release. One previous
server is retained for:

```bash
./housecarl-amethyst-install --rollback --both
```

To remove binaries, installed skills, and MCP registrations:

```bash
./housecarl-amethyst-install --uninstall --both
```

Uninstall deliberately preserves the XDG configuration directory.

## Deferred tools

PapyrusCompiler and BSArch process execution remain post-v1 Proton work.
Native record editing, BSA reading/listing/extraction, PEX decompilation, NIF
inspection, and asset resolution do not require Proton.

## License and acknowledgments

houseCARL-Amethyst is GPL-3.0-only. See `LICENSE` and
`THIRD-PARTY-NOTICES.txt`.

This fork is based on [houseCARL](https://github.com/Avick3110/houseCARL) by
[Avick3110](https://github.com/Avick3110). It retains upstream history,
licence notices, original attribution, the Mutagen record engine, MCP tools,
safety model, probes, and bundled skills.

It integrates [Amethyst Mod Manager](https://github.com/ChrisDKN/Amethyst-Mod-Manager)
by ChrisDKN and uses [Mutagen](https://github.com/Mutagen-Modding/Mutagen) by
Noggog.
