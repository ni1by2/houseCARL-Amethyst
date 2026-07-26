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
- [x] Ship the standalone discovery/setup command with Session 6 packaging.

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

### Documentation pass — active core complete; product edges remain

Baseline scope on 2026-07-22: 217 C# files, about 76,874 lines and roughly 722
function declarations. Existing upstream documentation is extensive but uneven;
presence of XML comments in a file is not evidence that every declaration or
non-obvious invariant has been reviewed.

- [x] Define the plain-English code documentation standard and completion gate.
- [x] Document the fork's hardlink redeployment model and its safety probe.
- [x] Review all fork-authored Amethyst layout, filemap, load-order and path code.
- [x] Review the fork-authored Amethyst MCP connection, status and refresh seams.
- [x] Review the inherited asset resolution and archive-discovery boundary.
- [x] Review all inherited `housecarl-core` declarations and reasoning seams.
- [ ] Review all inherited and fork-authored `housecarl-mcp` declarations.
- [x] Review all `housecarl-setup` declarations during the Linux rewrite.
- [ ] Review all generator, proof-harness and probe declarations.
- [ ] Run a final stale MO2/Windows terminology and XML-reference audit.
- [ ] Record per-component build/probe evidence and unresolved ambiguities.

Exit gate: every declaration under `src/` has been inspected against
`standards/HOUSECARL_CODE_DOCUMENTATION.md`, every non-obvious invariant has a
nearby explanation, the solution builds, relevant probes pass, and the roadmap
contains evidence for every component. This gate covers unchanged upstream code.

### Session 6 — Linux packaging and host integration

- [x] Replace Windows setup/build scripts with Linux equivalents.
- [x] Add standalone Amethyst discovery and atomic manifest setup.
- [x] Publish self-contained `linux-x64` with server trimming disabled.
- [x] Install under XDG data paths and register Codex/Claude MCP stdio.
- [x] Add config backups, atomic activation, uninstall, rollback and checksums.
- [ ] Remove Windows runtime, `.exe`, registry, MO2 and PowerShell-only surface.
- [x] Update bundled skills and release-facing documentation for Amethyst.

Exit gate: clean Linux installs start from both hosts without system .NET, and
upgrade/uninstall preserve user configuration.

### Session 7 — CI, documentation, and v1 release candidate

- [x] Move the required workflow definition to Ubuntu.
- [ ] Confirm the new Ubuntu workflow on GitHub and run all CI-safe probes.
- [x] Add installer-lifecycle and end-to-end synthetic Amethyst tests.
- [x] Add reproducible package validation and leak scanning.
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

- Active milestone: Session 6, completing the human-readable code audit after
  removal of inherited MO2 runtime seams.
- Last completed checkpoint: every public declaration in the shipped core,
  MCP server, and Linux installer now passes the strict CS1591 documentation
  gate. The MCP pass documents every tool entry point, request/result model,
  enum member, factory, constructor, and disposable service boundary. Live
  core and MCP remain free of MO2 runtime types and terminology.
- Verification performed: strict documentation builds pass for
  `housecarl-core`, `housecarl-mcp`, and `housecarl-setup`; core and setup have
  zero warnings, while MCP retains one intentional Mutagen obsolete-API
  warning unrelated to documentation. Full Linux `ci-all` passes 110/110 when
  `DOTNET_ROOT` names the repository-local .NET 9 runtime. A first run without
  that variable produced 109/110 because `binding-shim-guard` launches the MCP
  apphost, which correctly reported that it could not locate a system runtime;
  the isolated guard and full suite both pass with the required environment.
- Files/components changed: all MCP tool classes, public wire models and
  service outcomes in `LoadOrderService`, Nexus result enums and client
  construction, plus the earlier runtime removal and synthetic fixture seams.
- Decisions made: test-only explicit paths remain available to synthetic
  probes, but are no longer a shipped server configuration. The obsolete
  update cache is removed instead of adapting MO2-specific `meta.ini` fields
  that Amethyst does not own. Historical changelog entries remain historical.
- Known failures or residual risks: the strict generator documentation audit
  exposes 109 inherited public probe/generator declarations. Most source files
  already contain detailed class-level and inline rationale, but their entry
  functions need concise local contracts before the full-project
  declaration-by-declaration gate is honest. Inherited generator probes still
  create legacy-shaped directories and use a generator-only parser to derive
  native roots; this does not enter release artifacts but should be simplified.
  A parallel MSBuild invocation can fail silently while evaluating shared
  project references, whereas `-m:1` succeeds; this needs a focused build-graph
  diagnosis before CI is changed. The Ubuntu workflow definition has not yet
  been observed green on GitHub. A disposable real
  Skyrim/Amethyst hardlink profile remains the Session 7 manual release gate.
  External PapyrusCompiler and BSArch execution remains post-v1. NuGet
  vulnerability metadata remains unreachable in the restricted environment.
- Exact next action: document the 109 generator/probe entry declarations
  without weakening CS1591 or adding repetitive filler. Reuse each probe's
  existing detailed rationale and add a short local function contract stating
  what is checked, what input is accepted, and what the exit code means. Then
  simplify the generator-only legacy fixture parser and diagnose the parallel
  build failure as separate focused changes.
- Commits: `82923a2` (upstream v1.8.1 merge), `ae4fb18` (layout foundation),
  `6ec4ea7` (runtime connection), `45a498c` (runtime roadmap), `d00115c`
  (native Amethyst load order), `4560247` (authoritative filemap and asset
  resolution), `5cbda36` (hardlink-safe redeployment), `0928140` (documentation
  standard and first slice), `196df59` (Amethyst foundation contracts),
  `efacda9` (foundation checkpoint), `1b4c7e8` (Amethyst MCP lifecycle),
  `95f9a83` (asset resolution contracts), `e3ec7a1` (external archive boundary).
  `27ba3d4` merges upstream through `c305b07` while retaining the Amethyst
  safety and path contracts; `abaa5a1` records the Linux failure audit;
  `80c10c4` normalizes those probes and establishes the 111/111 Linux suite;
  `b41069c` completes the `LoadOrderResolver` documentation checkpoint;
  `5a4ad7d` completes the `ReadEngine` documentation checkpoint; `e468879`
  completes the `FieldsDiff` and `FieldPredicate` documentation checkpoint;
  `23c7c9a` completes the `EffectChain` and `ErrorCheck` documentation
  checkpoint; `0b85b13` completes the shared identity-utility documentation
  checkpoint; `16b809a` completes the schema-metadata documentation checkpoint;
  `9c3c5f3` completes the storage and ownership documentation checkpoint;
  `9a5c4d0` replaces the inherited README; `67bbaf5` publishes the integrated
  history to `amethyst-main`; `be0e394` completes the canonical asset-path
  documentation checkpoint; `973083e` completes the asset-provider
  documentation checkpoint; `fabf847` completes the asset-carry documentation
  checkpoint; `e3adb8c` completes the NIF and dialogue-diagnostics
  documentation checkpoint; `5c0039f` completes the dialogue-semantics
  documentation checkpoint; `a6a8cb1` completes the supporting-diagnostics
  documentation checkpoint; `073d409` completes the Papyrus-toolchain
  documentation checkpoint; `5274457` completes the `WritePatchBuilder`
  public-contract checkpoint; `ed72cd5` completes the first internal
  implementation slice; `ac3b1be` completes the removal-and-forwarding slice.
  `5a1fbb2` completes the header-only creation, compaction, and merge-build
  slice; `465436b` completes `WritePatchBuilder`; `d16d990` completes
  `CorpusRulebook`; `e61882b` completes `RemapEngine`; `52b66cf` completes the
  public `WriteEngine` slice; `2cf70a4` completes the lifecycle slice;
  `06207b1` completes `WriteEngine`; `8f70b4e` completes
  `SkyPatcherCatalog`; `0917bea` completes the Linux installer and release
  checkpoint; `22c431d` records its evidence; `e493098` completes standalone
  Amethyst discovery and atomic manifest creation; `3bee8fc` records that
  checkpoint; `40c867f` removes the first obsolete MO2 product surfaces;
  `4270be4` records that runtime slice; `a194650` removes `Mo2LoadOrder`.
  `feefe53` records that helper migration; `a25aac4` removes live
  `Mo2Instance` mode and isolates inherited fixture parsing outside runtime;
  `4e4ec6a` records the runtime removal checkpoint; `f50465b` documents the
  complete shipped MCP public surface.
- Draft pull requests: #2 upstream integration; #3 layout foundation; #4
  runtime connection; #5 native Amethyst load order; #6 authoritative filemap
  and asset resolution; #7 connector retirement; #8 hardlink-safe writes.

## Linux failure audit and normalization checkpoint — complete

The 104/111 full-suite result was investigated and normalized on 2026-07-23:

| Probe | Finding | Resolution |
| --- | --- | --- |
| `writelock-guard` | Its red control required Windows mandatory sharing behaviour. | Windows retains the lock assertion; Linux proves mapped-path replacement succeeds. |
| `upsert-guard` | Its final arm expected a read handle to block replacement. | Linux proves the new record lands and staging residue is removed. |
| `compile-ergonomics-guard` | Fixtures and output logic used Windows/MO2 paths. | Tests use native host paths, staging deployability is Amethyst-specific, comparisons respect Linux case, and compiler execution is explicitly deferred. |
| `setup-update-lock-guard` | Linux reports an in-process sharing conflict as EAGAIN/11 rather than a Windows HRESULT. | Only that Linux lock value and the two Windows lock HRESULTs map to `ServerInUse`; unrelated I/O errors remain distinct. Full installer replacement remains Session 6. |
| `atomic-commit-guard` | Creation-time preservation and mandatory locks are Windows invariants. | The portable contract is documented; Linux proves new-path/old-handle inode semantics plus byte-exact staging. |
| `seq-regen-guard` | `FileShare.None` does not block Linux replacement. | Windows proves the warning lane; Linux proves successful refresh without a false warning. |
| `skypatcher-conflicts-guard` | Linux `Path.GetFileName` cannot split canonical backslash paths. | The assertion uses `BethesdaPath.FileName`; duplicate detection is green. |

Normalization exit gate:

- No Linux probe relies on Windows drive syntax, mandatory file sharing, NTFS
  creation-time preservation, or Linux `Path` splitting of Bethesda paths.
- The five platform-assumption probes have explicit Linux evidence rather than
  unconditional skips.
- SkyPatcher duplicate detection is green with canonical Bethesda paths.
- Compilation is either honestly excluded/deferred or has a native Amethyst
  output-path contract.
- The transitional setup lock probe is green; Session 6 replaces the installer
  and its suite rather than treating this as final packaging evidence.
- `ci-all` is 111/111 on Linux; no permanent red-baseline allowance remains.

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
