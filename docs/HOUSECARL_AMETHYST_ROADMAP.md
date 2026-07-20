# houseCARL-Amethyst Linux Roadmap

This is the durable source of truth for the Linux-only Amethyst fork of
houseCARL. Update it at the end of every implementation session. A milestone is
complete only when its exit gate is supported by recorded evidence.

## Product decisions

- Product: **houseCARL-Amethyst**, GPL-3.0-only.
- Platform: Linux x86_64, distributed as a self-contained bundle.
- AI hosts: Codex and Claude Code over MCP stdio.
- Manager: Amethyst only; the public MO2/Windows surface will be removed.
- Connection setup: a standalone command shipped inside houseCARL-Amethyst.
  No Amethyst-side plugin or companion product is required.
- Profiles: shared and profile-specific staging are both required.
- Deployment: hardlinks are the v1 release gate. Symlink and copy modes receive
  smoke coverage when this adds no production branching. Amethyst has no VFS
  deployment backend, so VFS is out of scope.
- Writes: new patch mods by default. In-place editing retains upstream consent
  guards and adds explicit Amethyst redeployment confirmation and verification.
- PapyrusCompiler and BSArch process execution through Proton are post-v1.
- Code style: keep production code sparse, concise, and easy to audit. Put
  extensive explanation in architecture/contract documents; use inline comments
  only for non-obvious invariants, safety boundaries, or format provenance.

## Pinned upstream inputs

- houseCARL: `3fb962b87137c1ff25db0a191c7e27a4a5215da2`
- Amethyst Mod Manager: `30f4efb5349e95f04968fca9b809130a34f6032a`
- Fork directory: `houseCARL-Amethyst/`

## Architecture contracts

### Connection manifest

The bundled standalone setup command writes
`<profile-root>/.housecarl-amethyst/connection.json` atomically. Schema v1:

```json
{
  "schemaVersion": 1,
  "manager": "amethyst",
  "gameId": "skyrim_se",
  "gameName": "Skyrim Special Edition",
  "profileRoot": "/absolute/native/path",
  "gameConfigDir": "/absolute/native/path",
  "pathsFile": "/absolute/native/path/paths.json",
  "deployStateFile": "/absolute/native/path/deploy_state.json",
  "createdBy": "housecarl-amethyst-setup",
  "setupVersion": "1.0.0"
}
```

All paths are absolute Linux paths. The manifest contains stable roots; the
server recalculates the active profile and its effective paths from Amethyst
state. Missing, malformed, or unknown-version inputs fail loudly.

### Effective layout

The active profile is `deploy_state.json.last_active_profile`, defaulting to
`default` only when the field is absent. The profile directory is
`profileRoot/profiles/<profile>`. The profile's
`profile_state.json.profile_settings.profile_specific_mods` selects either:

- shared: `profileRoot/{mods,overwrite,filemap.txt,modindex.bin}`; or
- specific: `profileDir/{mods,overwrite,filemap.txt,modindex.bin}`.

The profile's `game_path` override wins over `paths.json.game_path`.
`<game>/Data_Core` is vanilla while deployed. `<game>/Data` is accepted only
when deployment is inactive. A deployed state without `Data_Core` is an error.

### Source truth

- `modlist.txt`: `+` and `*` are enabled; `*` is locked; `-` is disabled.
- `plugins.txt`: Skyrim star-prefix activation.
- `loadorder.txt`: authoritative plugin order.
- `filemap.txt`: authoritative loose-file winners.
- `modindex.bin` MessagePack v4: maps normalized winner keys to raw-cased
  staging paths. `[Overwrite]` maps to `overwrite/`.
- Deployed `Data/` is never used to infer winners or provenance.

### Paths and writes

Bethesda paths remain canonical backslash paths for plugin fields and archive
entries. Every host filesystem operation converts validated path segments to
native Linux paths. Absolute, drive-prefixed, NUL-containing, empty-segment,
and parent-escaping relative paths are refused.

Default output is `<mods>/houseCARL - <name>/<name>.esp`. houseCARL-Amethyst
does not edit Amethyst's order or filemap files. Every write enters a pending
refresh/enable/deploy state. In-place editing additionally requires
`confirm_amethyst_redeploy=true`, captures hardlink identity before replacement,
writes staging only, and clears pending state only after a later verified deploy.

## Milestones

### Session 0 — Fork, baseline, and roadmap

- [x] Clone the pinned upstream into `houseCARL-Amethyst/`.
- [x] Configure the original repository as `upstream`.
- [ ] Add a hosted fork as `origin` when GitHub authentication is available.
- [x] Create this roadmap and the Windows/MO2 assumption inventory below.
- [x] Install an isolated .NET 9 SDK and capture the unmodified Linux build.
- [x] Run CI-safe probes and classify failures.

Exit gate: baseline build/probe evidence and complete assumption ownership.

### Session 1 — Native Linux path foundation — complete

- [x] Add explicit Bethesda-path validation and host-path conversion.
- [x] Replace filesystem use of canonical backslash paths in the current asset,
  placement, appearance, NIF, voice, script, SkyPatcher, and SEQ seams.
- [x] Cover nested assets, placement, facegen, voice, scripts, NIF and SEQ.
- [x] Add case-sensitive Linux fixtures with mixed separators/case, spaces,
  Unicode, deep paths, and invalid-path guards.

Exit gate: Linux build and probes pass; nested asset tests pass; startup makes
no Windows registry or drive assumptions.

### Session 2 — Connection manifest and runtime seam

- [x] Define and validate the stable schema-v1 connection manifest.
- [x] Keep changing profile and deployment state out of the manifest.
- [x] Cover shared and profile-specific layouts with synthetic tests.
- [x] Retire the experimental Amethyst-side connector design.
- [ ] Ship the standalone discovery/setup command with Session 6 packaging.

Exit gate: a setup-generated manifest remains usable across restarts and
invalid state produces actionable diagnostics without modifying Amethyst.

### Session 3 — Amethyst profile and load-order adapter

- [x] Add `IModManagerLayout`, `ManagerSnapshot`, and `AmethystLayout`.
- [x] Read connection, path, deployment, profile, mod and plugin state.
- [x] Support shared/profile-specific staging and `*` locked mods.
- [x] Resolve plugins from staging/overwrite and vanilla `Data_Core`.
- [x] Replace public MO2 configuration/status with Amethyst MCP tools.

Exit gate: synthetic order matches Amethyst, profile switching refreshes without
restart, and deployed `Data/` is never classified as vanilla.

### Session 4 — Authoritative filemap and asset resolution

- [x] Parse `filemap.txt` and MessagePack `modindex.bin` v4.
- [x] Resolve normalized winners to raw-cased staging files.
- [x] Support overwrite, exclusions, strip-prefix output and casing rewrites.
- [x] Drive archive discovery and asset provenance from the snapshot.
- [x] Add all relevant files to freshness checks and refuse stale mismatches.

Exit gate: loose/loose and loose/BSA winners, overwrite, excluded files,
strip-prefix mods, facegen, voice, NIF, SKSE and SkyPatcher fixtures pass.

### Session 5 — Writes and hardlink-safe redeployment

- [ ] Write generated mods into effective Amethyst staging.
- [ ] Add Amethyst refresh/enable/deploy results and pending state.
- [ ] Add inode/device capture and deployed-copy diagnostics.
- [ ] Add `confirm_amethyst_redeploy` to guarded in-place calls.
- [ ] Verify and clear pending state only after a later deployment.
- [ ] Add hardlink release tests and symlink/copy smoke tests.

Exit gate: new and guarded in-place writes never target deployed `Data/`, report
old hardlinks honestly, and clear pending state only after verification.

### Session 6 — Linux packaging and host integration

- [ ] Replace Windows setup/build scripts with Linux equivalents.
- [ ] Add standalone Amethyst discovery and atomic manifest setup.
- [ ] Publish self-contained `linux-x64` with trimming disabled.
- [ ] Install under XDG data paths and register Codex/Claude MCP stdio.
- [ ] Add backups, update locks, uninstall, rollback and checksums.
- [ ] Remove Windows runtime, `.exe`, registry, MO2 and PowerShell-only surface.
- [ ] Update all bundled skills and user-facing documentation.

Exit gate: clean Linux installs start from both hosts without system .NET, and
upgrade/uninstall preserve user configuration.

### Session 7 — CI, documentation, and v1 release candidate

- [ ] Move required CI to Ubuntu and run all CI-safe probes.
- [ ] Add standalone-setup and end-to-end synthetic Amethyst tests.
- [ ] Add reproducible package validation and leak scanning.
- [ ] Complete installation, workflow, safety and troubleshooting docs.
- [ ] Perform disposable real-profile hardlink validation.
- [ ] Produce the v1 checklist and rollback procedure.

Exit gate: Ubuntu CI is green and real hardlink validation proves read, new
patch, redeploy and guarded in-place workflows.

### Post-v1 — Proton external tools

- [ ] Replace `.exe` paths with structured command/argument/environment specs.
- [ ] Add Proton-backed PapyrusCompiler and BSArch validation.
- [ ] Keep all native local capabilities independent of Proton.

## Windows/MO2 assumption inventory

| Boundary | Evidence | Owner |
|---|---|---|
| MO2 instance derivation | `Mo2Instance.cs` reads `ModOrganizer.ini` and Windows paths | Session 3 |
| Mod/plugin composition | `Mo2LoadOrder.cs` recognizes only `+`/`-` mods | Session 3 |
| Asset host paths | `AssetResolver.cs` combines canonical `\` paths with host roots | Session 1 |
| Appearance host paths | `NpcAppearanceAssets.cs` combines Bethesda-relative paths directly | Session 1 |
| Write output paths | `LoadOrderService.cs` uses canonical relative paths in host combines | Sessions 1/5 |
| Game discovery | `LoadOrderService.CompilerGameDirHints` uses Mutagen GameFinder/registry | Post-v1 |
| External tools | `ToolBridge` requires `.exe`; process riders launch it directly | Post-v1 |
| Installer destinations | setup uses `%LOCALAPPDATA%`, `.exe`, Windows lock semantics | Session 6 |
| Runtime detection | setup scans Windows Program Files and recommends winget | Session 6 |
| Packaging | `build-plugin.ps1` publishes `win-x64` and setup EXE | Session 6 |
| CI | workflow runs only `windows-latest` | Sessions 0/7 |
| Skills/docs | README and skills name Windows/MO2 throughout | Sessions 6/7 |
| Generated mod messaging | output says refresh/enable in MO2 | Session 5 |
| Tool log discovery | `ToolBridge.MyGames` assumes Windows Documents layout | Post-v1 |

## Required test matrix

- Shared and profile-specific mods; default and non-default profiles.
- Active-profile switching; deployed and restored hardlink states.
- `+`, `-`, and `*` mod entries; active/inactive/vanilla/CC/light plugins.
- Overwrite plugins/assets; duplicate names; missing or stale state files.
- Filemap/mod-index mismatch and unknown versions.
- Exclusions, strip prefixes, mixed case, spaces, Unicode and mounted paths.
- Loose/BSA conflicts, generated ownership and cleanup.
- In-place hardlink identity before/after atomic replacement and redeploy.
- Native/AppImage, AUR-style and Flatpak Amethyst discovery paths.
- Codex and Claude installation and startup.

CI fixtures contain no copyrighted Skyrim data. Real-game validation is a
manual release gate.

## Current status

- Active milestone: Session 4 complete; Session 5 is next.
- Last completed checkpoint: strict Amethyst `filemap.txt` and MessagePack
  `modindex.bin` v4 parsing now drives plugin, loose-asset and BSA source
  resolution using raw Linux casing and staging paths.
- Verification performed: the Linux solution and generator build with .NET 9.
  The Amethyst filemap guard covers loose/loose winners, loose/BSA precedence,
  overwrite, exclusions, global and per-mod strip prefixes, mixed case,
  Unicode, vanilla fallback, archive discovery, stale/missing state, unknown
  versions, traversal, and vanished winners. Existing asset, facegen, voice,
  SKSE, SkyPatcher and runtime guards pass. Full `ci-all` is 89/98; its nine
  failures exactly match the validated upstream Linux baseline.
- Files/components changed: `AmethystFileMap`, `AmethystLayout`,
  `AmethystLoadOrder`, `ArchiveDiscovery`, `AssetResolver`,
  `IModManagerLayout`, `LoadOrderService`, the runtime/filemap probes, package
  metadata, and third-party notices.
- Decisions made: `filemap.txt` is the only loose winner authority.
  `modindex.bin` v4 `rel_str` preserves casing after Amethyst's strip operation;
  it is not always a complete physical source-relative path. Source lookup
  therefore mirrors Amethyst's direct, global `Data`, and configured per-mod
  wrapper reconstruction. Unknown versions, stale pairs, mismatches, or
  vanished winners fail instead of scanning staging or deployed `Data/`. The
  earlier companion connector design is superseded: connection setup belongs
  in the main product and Amethyst requires no houseCARL plugin.
- Known failures or residual risks: real Amethyst and Skyrim validation remains
  a manual release gate. The standalone discovery/setup command is not yet
  shipped; it is a Session 6 packaging deliverable. The obsolete connector
  repository is pending confirmed deletion. MessagePack is a new MIT-licensed
  dependency. Windows external-tool and installer paths remain deferred. The
  nine baseline failures are `writelock`, `upsert`,
  `binding-shim`, `compile-ergonomics`, `setup-update-lock`, `bsa-contract`,
  `atomic-commit`, `seq-regen`, and `skypatcher-conflicts`.
- Exact next action: begin Session 5 by routing new patch folders into effective
  Amethyst staging and adding the pending-refresh/enable/deploy state contract.
- Commits: `82923a2` (upstream v1.8.1 merge), `ae4fb18` (layout foundation),
  `6ec4ea7` (runtime connection), `45a498c` (runtime roadmap), `d00115c`
  (native Amethyst load order), `4560247` (authoritative filemap and asset
  resolution).
- Draft pull requests: #2 upstream integration; #3 layout foundation; #4
  runtime connection; #5 native Amethyst load order; #6 authoritative filemap
  and asset resolution.

## Session update template

```markdown
## Current status

- Active milestone:
- Last completed checkpoint:
- Verification performed:
- Files/components changed:
- Decisions made:
- Known failures or residual risks:
- Exact next action:
- Commit(s):
```

Keep commits focused, add failing-before/passing-after probes for behavioral
changes, and never silently fall back from invalid Amethyst state to scanning
deployed `Data/`.
