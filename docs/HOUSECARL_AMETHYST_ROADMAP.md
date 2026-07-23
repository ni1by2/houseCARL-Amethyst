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
- Code style: keep implementation sparse, concise, and easy to audit. Every
  inherited and fork-authored C# declaration receives a plain-English XML
  contract; non-obvious reasoning is explained inline without narrating syntax.
  `standards/HOUSECARL_CODE_DOCUMENTATION.md` defines the completion gate.

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
- [x] Add the hosted `ni1by2/houseCARL-Amethyst` fork as `origin`.
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

### Session 5 — Writes and hardlink-safe redeployment — complete

- [x] Write generated mods into effective Amethyst staging.
- [x] Add Amethyst refresh/enable/deploy results and pending state.
- [x] Add inode/device capture and deployed-copy diagnostics.
- [x] Add `confirm_amethyst_redeploy` to guarded in-place calls.
- [x] Verify and clear pending state only after a later deployment.
- [x] Add hardlink release tests and symlink/copy smoke tests.

Exit gate: new and guarded in-place writes never target deployed `Data/`, report
old hardlinks honestly, and clear pending state only after verification.

### Documentation pass — complete inherited and fork-authored codebase — active

Baseline scope on 2026-07-22: 217 C# files, about 76,874 lines and roughly 722
function declarations. Existing upstream documentation is extensive but uneven;
presence of XML comments in a file is not evidence that every declaration or
non-obvious invariant has been reviewed.

- [x] Define the plain-English code documentation standard and completion gate.
- [x] Document the fork's hardlink redeployment model and its safety probe.
- [x] Review all fork-authored Amethyst layout, filemap, load-order and path code.
- [x] Review the fork-authored Amethyst MCP connection, status and refresh seams.
- [x] Review the inherited asset resolution and archive-discovery boundary.
- [ ] Review all inherited `housecarl-core` declarations and reasoning seams.
- [ ] Review all inherited and fork-authored `housecarl-mcp` declarations.
- [ ] Review all `housecarl-setup` declarations during the Linux rewrite.
- [ ] Review all generator, proof-harness and probe declarations.
- [ ] Run a final stale MO2/Windows terminology and XML-reference audit.
- [ ] Record per-component build/probe evidence and unresolved ambiguities.

Exit gate: every declaration under `src/` has been inspected against
`standards/HOUSECARL_CODE_DOCUMENTATION.md`, every non-obvious invariant has a
nearby explanation, the solution builds, relevant probes pass, and the roadmap
contains evidence for every component. This gate covers unchanged upstream code.

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

- Active milestone: full-codebase documentation pass; Session 6 follows it, with
  setup documentation to be completed alongside the Linux setup rewrite.
- Last completed checkpoint: merged upstream houseCARL through
  `c305b07da937d44d113a058f89d5292670ddcc0e` (v1.9.0 plus 58 later commits)
  into the Amethyst integration branch. The fork now carries upstream native BSA
  list/extract, SKSE audit and peek, NIF batch/section inspection, dry-run,
  manifest-backed bulk writes, readback counts, native pairing, and the latest
  provenance corrections. Amethyst plugin provenance resolves only from
  `ManagerSnapshot`, `filemap.txt`, `modindex.bin`, and `Data_Core`; it never
  reintroduces an MO2 folder scan or deployed-Data fallback.
- Verification performed: the .NET 9 Linux solution builds with zero errors.
  The focused Amethyst layout, load-order, filemap, runtime, redeploy, dry-run,
  BSA contract/extract, NIF batch/sections, SKSE peek/config-audit,
  native-pairing, argument-binding, readback-count, raw-plugin-read, provenance,
  and Codex umbrella guards pass. Full `ci-all` is 104/111; its seven failures
  match the established Linux baseline. XML-document generation succeeds while
  suppressing only missing-member warning CS1591; inherited unresolved-cref
  warnings remain queued for the declaration-by-declaration documentation pass.
  `git diff --check` passes and no merge-conflict markers remain.
- Files/components changed: the upstream v1.9/post-v1.9 source, probes, skills,
  notices, changelog, and metadata; Amethyst conflict resolutions in
  `ArchiveDiscovery`, `AssetResolver`, `BsaArchive`, `LoadOrderService`,
  `Program`, and write/BSA/NIF tool surfaces; Linux-neutral BSA, binding, and
  SKSE probe fixtures; Codex umbrella routing and agent metadata.
- Decisions made: staging is the only write target; deployed `Data/` is used
  solely for later verification. Upstream native archive reads replace the
  Windows-only BSArch dependency for list/extract; archive packing remains the
  explicitly deferred external-command seam. Upstream dry-run may bypass the
  Amethyst redeploy confirmation only because it performs no write. Real
  in-place writes still require both consent and
  `confirm_amethyst_redeploy=true`. Legacy MO2 provenance code remains only for
  inherited probes; connected product mode uses authoritative Amethyst winners.
- Known failures or residual risks: a disposable real Skyrim/Amethyst hardlink
  profile remains the Session 7 manual release gate. Session 6 must replace the
  remaining Windows installer/runtime surface and MO2 terminology. The
  declaration-by-declaration review is still incomplete even though the strict
  XML build succeeds; CS1591 remains intentionally suppressed until each
  component has been reviewed. The seven baseline failures are `writelock`,
  `upsert`, `compile-ergonomics`, `setup-update-lock`, `atomic-commit`,
  `seq-regen`, and `skypatcher-conflicts`.
- Exact next action: continue the inherited `housecarl-core` pass with
  `LoadOrderResolver` and its directly coupled result/snapshot records, then run
  its strict CS1591 build and resolver guards. Document setup code alongside its
  Session 6 Linux rewrite rather than preserving stale Windows contracts.
- Commits: `82923a2` (upstream v1.8.1 merge), `ae4fb18` (layout foundation),
  `6ec4ea7` (runtime connection), `45a498c` (runtime roadmap), `d00115c`
  (native Amethyst load order), `4560247` (authoritative filemap and asset
  resolution), `5cbda36` (hardlink-safe redeployment), `0928140` (documentation
  standard and first slice), `196df59` (Amethyst foundation contracts),
  `efacda9` (foundation checkpoint), `1b4c7e8` (Amethyst MCP lifecycle),
  `95f9a83` (asset resolution contracts), `e3ec7a1` (external archive boundary).
  The upstream `c305b07` merge commit is recorded after this checkpoint is
  committed.
- Draft pull requests: #2 upstream integration; #3 layout foundation; #4
  runtime connection; #5 native Amethyst load order; #6 authoritative filemap
  and asset resolution; #7 connector retirement; #8 hardlink-safe writes.

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
