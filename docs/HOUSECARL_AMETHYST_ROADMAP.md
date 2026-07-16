# houseCARL-Amethyst Linux Roadmap

This is the durable source of truth for the Linux-only Amethyst fork of
houseCARL. Update it at the end of every implementation session. A milestone is
complete only when its exit gate is supported by recorded evidence.

## Product decisions

- Product: **houseCARL-Amethyst**, GPL-3.0-only.
- Platform: Linux x86_64, distributed as a self-contained bundle.
- AI hosts: Codex and Claude Code over MCP stdio.
- Manager: Amethyst only; the public MO2/Windows surface will be removed.
- Connection setup: required, but it must not depend on an Amethyst-side Python
  plugin. A sparse Linux setup command will validate Amethyst state and write
  the versioned manifest. The existing connector repository remains the
  migration source until that command is shipped.
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
- Audited houseCARL sync target: `248e69b` (`v1.8.1`, 2026-07-16)
- Audited Amethyst target: `40a96c0` (`v2.0.4-beta.1`, Testing, 2026-07-16)
- Fork directory: `houseCARL-Amethyst/`
- Companion directory: `housecarl-amethyst-connector/` (created in Session 2)

## Architecture contracts

### Connection manifest

The required connection setup writes
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
  "createdBy": "housecarl-amethyst-connector",
  "connectorVersion": "1.0.0"
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

### Session 2 — Amethyst connection bootstrap

- [x] Create the separate connector product/repository.
- [x] Implement the `skyrim_se` external wizard plugin.
- [x] Validate and atomically export the v1 manifest.
- [x] Package native/AppImage and Flatpak plugin installers plus uninstall.
- [x] Confirm Amethyst's v2.0.4 `Refresh Plugins` action only refreshes LOOT
  metadata for Skyrim plugins and is unrelated to external Python plugins.
- [ ] Move validation and atomic manifest export into a standalone Linux setup
  command owned by houseCARL-Amethyst.
- [ ] Add native/AppImage and Flatpak `paths.json` discovery plus an explicit
  path override; require confirmation when discovery is ambiguous.
- [ ] Deprecate the Amethyst Python wizard and plugin-directory installers after
  the standalone command passes equivalent synthetic and real-install tests.

Exit gate: setup requires no Amethyst extension, its manifest remains usable
across restarts, and invalid or ambiguous state produces actionable diagnostics.

### Session 2A — Deliberate upstream v1.8.1 integration — queued prerequisite

- [x] Audit upstream `3fb962b..248e69b`: 66 commits, 60 changed files, 6,493
  insertions and 255 deletions.
- [x] Prove the synthetic merge has no textual conflicts and builds
  `housecarl-core`, `housecarl-mcp`, and `housecarl-setup` on Linux.
- [ ] Merge `v1.8.1` in one focused upstream-integration commit before further
  Amethyst adapter work.
- [ ] Preserve the manager-neutral additions: bulk resolve/query/diff/read JSON,
  `CopyFrom`, composed list writes, flag-bit safety, presence predicates,
  off-order finishing, patch-name collision protection, and SkyPatcher fixes.
- [ ] Rebase new filesystem reads onto `BethesdaPath` and the future
  `IModManagerLayout`; rerun the case-sensitive Linux path probes.
- [ ] Adapt off-order plugin discovery to the Amethyst snapshot instead of
  MO2 folder scanning.
- [ ] Replace the MO2 `meta.ini` update-cache reader with an Amethyst metadata
  adapter or disable only that local-cache tool until its contract is proven.
- [ ] Run the full CI-safe probe set and compare failures with the recorded
  76/85 Linux baseline before marking the sync complete.

Exit gate: upstream v1.8.1 capabilities are retained except for explicitly
documented MO2-only behavior, Linux path guards remain green, and no public MO2
contract leaks into the Amethyst product surface.

### Session 3 — Amethyst profile and load-order adapter

- [ ] Add `IModManagerLayout`, `ManagerSnapshot`, and `AmethystLayout`.
- [ ] Read connection, path, deployment, profile, mod and plugin state.
- [ ] Support shared/profile-specific staging and `*` locked mods.
- [ ] Resolve plugins from staging/overwrite and vanilla `Data_Core`.
- [ ] Replace public MO2 configuration/status with Amethyst MCP tools.

Exit gate: synthetic order matches Amethyst, profile switching refreshes without
restart, and deployed `Data/` is never classified as vanilla.

### Session 4 — Authoritative filemap and asset resolution

- [ ] Parse `filemap.txt` and MessagePack `modindex.bin` v4.
- [ ] Resolve normalized winners to raw-cased staging files.
- [ ] Support overwrite, exclusions, strip-prefix output and casing rewrites.
- [ ] Drive archive discovery and asset provenance from the snapshot.
- [ ] Add all relevant files to freshness checks and refuse stale mismatches.

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
- [ ] Publish self-contained `linux-x64` with trimming disabled.
- [ ] Install under XDG data paths and register Codex/Claude MCP stdio.
- [ ] Add backups, update locks, uninstall, rollback and checksums.
- [ ] Remove Windows runtime, `.exe`, registry, MO2 and PowerShell-only surface.
- [ ] Update all bundled skills and user-facing documentation.

Exit gate: clean Linux installs start from both hosts without system .NET, and
upgrade/uninstall preserve user configuration.

### Session 7 — CI, documentation, and v1 release candidate

- [ ] Move required CI to Ubuntu and run all CI-safe probes.
- [ ] Add connector and end-to-end synthetic Amethyst tests.
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
- Native/AppImage and Flatpak connection-setup paths.
- Codex and Claude installation and startup.

CI fixtures contain no copyrighted Skyrim data. Real-game validation is a
manual release gate.

## Current status

- Active milestone: Session 2A — deliberate upstream v1.8.1 integration, before
  replacing the external connector with standalone connection setup.
- Last completed checkpoint: audited houseCARL `v1.8.1` and Amethyst
  `v2.0.4-beta.1`; confirmed Amethyst's new Refresh Plugins button is a LOOT
  metadata action and does not manage external Python plugins.
- Verification performed: .NET SDK 9.0.315 / runtime 9.0.17; generator and all
  referenced product projects build in Release with no errors; focused guards
  pass for asset resolution/status, nested creation, placement, NIF, facegen,
  voice, merge, NPC appearance, and SkyPatcher discovery. The final `ci-all`
  result is 76/85 passing in 0.24 minutes, up from the 67/85 baseline.
  Startup inspection confirms GameFinder/registry probing remains lazy behind
  external-tool requests and is not executed during MCP server construction.
  The synthetic `v1.8.1` merge reports no textual conflicts; core, MCP, and
  setup build on Linux. The generator restore graph still exits without a
  diagnostic in the disposable worktree, so the full probe suite remains an
  integration exit gate rather than claimed evidence.
- Files/components changed: `BethesdaPath`, asset/appearance/rename resolution,
  MCP placement paths, SkyPatcher gate extraction, Linux probe fixtures,
  `docs/PATH_MODEL.md`, and this roadmap.
- Decisions made: Bethesda paths are validated canonical strings; host I/O uses
  native segments; logical lookup is case-insensitive and returns raw host
  casing. Linux probes do not assert Windows creation-time semantics. Upstream
  `v1.8.1` must land before new adapter work. The manifest stays, but its writer
  moves out of Amethyst into a standalone houseCARL-Amethyst setup command.
- Known failures or residual risks: Windows file-lock, creation-time, `.exe`, and
  installer assumptions remain assigned to later milestones. The independent
  `skypatcher-conflicts` duplicate-reporting failure remains unrelated to this
  port. Session 1 does not replace MO2 startup/configuration; that public boundary
  is Session 3. Amethyst's Qt external-wizard seam remains fragile and is now
  scheduled for removal. Upstream's `housecarl_update_status` reads MO2-specific
  metadata, and its off-order plugin lookup scans MO2 folders; neither may enter
  the fork unchanged. The synthetic merge compiles the product projects but has
  not passed the full generator probe suite.
- Exact next action: merge upstream `v1.8.1` as a focused integration, adapt or
  gate the two MO2-specific surfaces, and run the Linux probes. Then move the
  connector core into standalone connection setup before `AmethystLayout`.
- Commits: `2e39b5d` (roadmap/baseline), `4e2d27a` (native Linux path
  boundary), `f010b7c` (milestone record); connector `6fcee5d` in its separate
  repository.
- Published repositories: `ni1by2/houseCARL-Amethyst` on default branch
  `amethyst-main`; `ni1by2/housecarl-amethyst-connector` on `main`.

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
