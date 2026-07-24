# houseCARL-Amethyst

**Native Linux access to a Skyrim Special Edition load order through Amethyst,
Claude Code, or Codex.**

houseCARL-Amethyst is a Linux-only fork of
[houseCARL](https://github.com/Avick3110/houseCARL). It replaces houseCARL's
Windows and Mod Organizer 2 integration with native
[Amethyst Mod Manager](https://github.com/ChrisDKN/Amethyst-Mod-Manager)
state, while preserving the Mutagen record engine, MCP tools, bundled skills,
reflection-driven record coverage, and guarded write model.

The server reads Amethyst's active profile, load order, staging directories,
`filemap.txt`, and `modindex.bin` directly. It does not require MO2, Wine,
Proton, USVFS, or an Amethyst-side houseCARL plugin.

> [!IMPORTANT]
> This fork is under active development and does not yet have a supported
> binary release. The native Amethyst read, asset-resolution, patch-writing,
> and hardlink-redeployment foundations are implemented and covered by the
> Linux test suite. The self-contained Linux installer, final host integration,
> and real-profile release validation remain in progress. See the
> [roadmap](docs/HOUSECARL_AMETHYST_ROADMAP.md).

## Why this fork exists

Original houseCARL reads MO2 profile files and ships a Windows installer. That
design is a poor fit for a native Linux modding environment even when Skyrim
itself runs through Proton.

Amethyst already owns the information houseCARL needs:

- the active Skyrim SE profile;
- enabled, disabled, and locked mods;
- authoritative plugin order;
- shared or profile-specific staging;
- loose-file winners and their real source paths;
- deployment state and deployment mode.

houseCARL-Amethyst consumes that state through a manager-specific layout
adapter. The record and asset engines receive one immutable manager snapshot
and do not contain Amethyst parsing logic.

## Current capability

The source tree currently provides:

- Amethyst connection, status, refresh, and active-profile switching;
- shared and profile-specific staging directories;
- `+`, `-`, and Amethyst `*` mod-list entries;
- plugin resolution from staging, overwrite, and vanilla `Data_Core`;
- authoritative loose-file resolution through `filemap.txt` and MessagePack
  `modindex.bin` v4;
- case-safe Linux access while preserving canonical Bethesda backslash paths;
- record reads, conflict trees, cross-plugin queries, diffs, and bulk tools;
- new patch mods written into Amethyst staging;
- guarded in-place editing with explicit Amethyst redeployment confirmation;
- pending-redeploy tracking and post-deployment content/hardlink verification;
- native BSA listing and extraction, NIF inspection, SKSE inspection,
  SkyPatcher analysis, FaceGen/voice handling, and SEQ generation;
- Codex and Claude Code MCP surfaces and bundled skills.

Upstream contains a much larger tool surface than this summary. Retaining a
feature in the source tree is not the same as declaring it release-ready on
Linux. The roadmap records which boundaries have native tests and which still
need packaging or real-game validation.

## Requirements

For development and source-based testing:

- Linux x86_64;
- .NET 9 SDK;
- a native Amethyst installation with a Skyrim Special Edition game entry;
- an Amethyst profile with a built filemap and mod index;
- Codex or Claude Code if you want to use the MCP server conversationally.

Hardlink deployment is the required v1 target. Symlink and copy deployment
have synthetic smoke coverage but are not release gates. Amethyst has no
MO2-style VFS deployment backend, so this fork does not implement or claim VFS
support.

## Build from source

The current development work lives on
`agent/upstream-c305b07-amethyst` until it is merged into the default branch.

```bash
git clone https://github.com/ni1by2/houseCARL-Amethyst.git
cd houseCARL-Amethyst
git switch agent/upstream-c305b07-amethyst
dotnet restore
dotnet build housecarl.sln
```

Run the MCP server over its default stdio transport with:

```bash
dotnet run --project src/housecarl-mcp
```

The source build is for developers and verifiers. Session 6 will replace the
inherited Windows setup application with a self-contained `linux-x64` bundle,
XDG installation paths, Codex/Claude registration, upgrade rollback, and
uninstall support.

## Connect to Amethyst

houseCARL-Amethyst uses a small, stable manifest so the server does not have to
guess which Amethyst installation or Skyrim entry you mean. No connector
repository or Amethyst plugin is required.

The final standalone discovery command is part of the unfinished Linux
installer. Until that lands, create:

```text
<Amethyst profile root>/.housecarl-amethyst/connection.json
```

with this schema:

```json
{
  "schemaVersion": 1,
  "manager": "amethyst",
  "gameId": "skyrim_se",
  "gameName": "Skyrim Special Edition",
  "profileRoot": "/absolute/native/path/to/the/Amethyst/staging/root",
  "gameConfigDir": "/absolute/native/path/to/AmethystModManager/games/Skyrim Special Edition",
  "pathsFile": "/absolute/native/path/to/paths.json",
  "deployStateFile": "/absolute/native/path/to/deploy_state.json",
  "createdBy": "housecarl-amethyst-setup",
  "setupVersion": "1.0.0"
}
```

All paths must be absolute native Linux paths. The manifest contains stable
roots only. On every refresh, the server reads `deploy_state.json` again and
recalculates the active profile and effective staging paths.

In Codex or Claude Code, call:

```text
housecarl_set_amethyst_connection
```

with the manifest's absolute path. Then use:

```text
housecarl_amethyst_status
housecarl_refresh
```

to inspect or refresh the connected profile.

Invalid schemas, stale paths, missing Amethyst state, and unsafe vanilla-data
layouts fail with named errors. The server never falls back to MO2 behavior or
guesses winners from deployed `Data/`.

## Amethyst source-of-truth rules

For the active profile:

- `loadorder.txt` supplies authoritative plugin order;
- `plugins.txt` supplies Skyrim activation state;
- `modlist.txt` supplies enabled, disabled, and locked mods;
- `filemap.txt` supplies authoritative loose-file winners;
- `modindex.bin` v4 recovers exact source-relative paths and Linux casing;
- `[Overwrite]` resolves to Amethyst's effective overwrite directory;
- `Data_Core` is the vanilla source while deployment is active.

If Amethyst reports an active deployment but `Data_Core` is absent, the server
fails instead of misclassifying the merged game `Data/` directory as vanilla.

## Paths on Linux

The code keeps two path domains separate:

- a **Bethesda path** is Data-relative, uses `\`, and compares
  case-insensitively;
- a **host path** is an absolute or relative native Linux filesystem path.

Bethesda paths remain unchanged in plugin fields, BSA entries, Skyrim-format
output, and MCP responses. Before filesystem access, each segment is validated
and converted to a host path. Absolute paths, drive-prefixed paths, empty
segments, NULs, and `..` traversal are rejected.

Logical winner lookup is case-insensitive, but filesystem access uses the raw
casing recorded by Amethyst's mod index.

## Writing patches

The default write target is a new Amethyst staging mod:

```text
<effective mods directory>/houseCARL - <patch name>/
├── <patch name>.esp
└── meta.ini
```

houseCARL-Amethyst does not edit `modlist.txt`, `plugins.txt`,
`loadorder.txt`, or `filemap.txt`.

After creating a patch:

1. Refresh Amethyst.
2. Enable the generated mod.
3. Rebuild Amethyst's filemap.
4. Deploy the profile.
5. Ask houseCARL-Amethyst to refresh before relying on game-visible results.

The server records the write as pending until a later Amethyst deployment can
be verified.

## Hardlink-safe in-place editing

In-place editing is deliberately harder to invoke than normal patch creation.
It requires:

- `in_place=true`;
- the inherited per-plugin consent handshake;
- `confirm_amethyst_redeploy=true`.

The server writes only to the staging plugin. It never writes through the
deployed game `Data/` path.

Atomic replacement gives the staging file a new inode. With hardlink
deployment, the existing deployed pathname may still point to the old inode
and therefore the old plugin content. This is expected: refresh and redeploy
through Amethyst before launching the game.

Pending state clears only after:

- the same Amethyst profile is active;
- deployment is active;
- filemap/deployment state is newer than the write; and
- the deployed file matches the new staging content or hardlink identity.

Until those checks pass, the server refuses to claim that the change is
game-visible.

## Development and verification

Run the complete CI-safe probe suite with:

```bash
dotnet run --project src/housecarl-generator -- ci-all
```

The current Linux baseline is 111/111 probes passing. CI fixtures are
synthetic and contain no copyrighted Skyrim data. A disposable real
Skyrim/Amethyst hardlink profile remains a manual v1 release gate.

The implementation is intentionally sparse. Public, internal, and private C#
declarations are being reviewed declaration by declaration and documented in
plain English. The review standard is
[HOUSECARL_CODE_DOCUMENTATION.md](standards/HOUSECARL_CODE_DOCUMENTATION.md).

## Not ready yet

The following remain explicitly unfinished:

- the self-contained Linux installer and release archive;
- automatic discovery and creation of the Amethyst connection manifest;
- final Codex and Claude Code host-registration workflows;
- removal of every inherited MO2/Windows message and test-fixture label;
- reproducible Ubuntu release CI and package leak scanning;
- real-game hardlink validation;
- Proton command specifications for PapyrusCompiler and BSArch execution.

PapyrusCompiler and BSArch process execution are post-v1 work. Native record
editing, BSA reading/listing/extraction, PEX decompilation, NIF inspection, and
other local features do not require Proton.

## Project documentation

- [Implementation roadmap](docs/HOUSECARL_AMETHYST_ROADMAP.md)
- [Code documentation standard](standards/HOUSECARL_CODE_DOCUMENTATION.md)
- [GPL-3.0 license](LICENSE)
- [Third-party notices](THIRD-PARTY-NOTICES.txt)

## License

houseCARL-Amethyst is licensed under **GPL-3.0-only**, preserving houseCARL's
license and provenance. See [LICENSE](LICENSE) and
[THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).

## Acknowledgments

- **[houseCARL](https://github.com/Avick3110/houseCARL)** and its developer,
  **[Avick3110](https://github.com/Avick3110)** — this project is a derivative
  Linux/Amethyst port of their original architecture, Mutagen record engine,
  MCP tools, safety model, probes, and bundled skills. The fork retains
  upstream history, GPL notices, and original copyright attribution.
- **[Mutagen](https://github.com/Mutagen-Modding/Mutagen)** by Noggog — the
  Bethesda-format library on which houseCARL and this fork are built.
- **[Amethyst Mod Manager](https://github.com/ChrisDKN/Amethyst-Mod-Manager)**
  by ChrisDKN — the native Linux mod manager and state model used by this fork.
- **[papyrus-index](https://github.com/BellCubeDev/papyrus-index)** by BellCube
  — source material for the bundled Papyrus reference skill.
- **DrHeisen** — contributor of the upstream Papyrus optimization, OAR
  authoring, and generated-tool awareness skills retained by this fork.
- **Zzyxzz** and **powerofthree** — maintainers whose public SkyPatcher, SPID,
  and KID documentation informs the bundled distributor references.
