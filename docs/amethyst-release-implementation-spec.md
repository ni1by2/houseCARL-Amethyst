# houseCARL Amethyst implementation specification

## Release status — 15 October 2026

[v2.0.5 is published as a Linux preview prerelease](https://github.com/ni1by2/houseCARL-Amethyst/releases/tag/v2.0.5) from `87e9268dc1052e31343fc8ff5bc014bd53b6c264`, including upstream 2.0.5. Use the [installation guide](linux-install.md) and [release verification record](testing/upstream-205-integration-2026-10-15.md) for current setup, completed checks and limitations. The adapter refactor remains deferred. Windows execution, the actual manager GUI workflow, live-profile deployment and gameplay were not tested.

The planning snapshot below preserves the earlier pins, proposals, test counts and then-outstanding gates. Its implementation requirements remain design context; its progress statements and candidate version are historical, not the status of the published release.

## Planning snapshot — 14 October 2026

Updated 14 October 2026. Status: Packages 1–5 are committed; later writer, runner, verification and installer work remains in the integration worktree for review. The complete Release Linux/Amethyst suite passes 7,136 tests with zero failures and one Windows handle/deletion skip. All 263 retained failures are resolved, and five formerly skipped compiler cases now exercise actual Caprica with authored sources, source imports, structured errors and byte-level recompilation checks. These real tests exposed and corrected Caprica diagnostic parsing and the pinned native compiler's PEX object-length field; strict Mutagen validation remains intact.

The Python installer/package suite passes 21/21. The pinned Caprica/Champollion build and synthetic compile/decompile smoke test pass, with source archives, licenses and correction recipe staged. Current development-host glibc 2.44 artifacts remain explicitly unsuitable for the Ubuntu 24.04/glibc 2.39 release archive.

Clean Ubuntu package installation/update/rollback, Arch-derived package acceptance, actual selected-Proton execution, explicitly authorized isolated deployment acceptance, independent review and publication cleanup remain outstanding. Windows execution is unverified and outside the user's selected Linux/Amethyst test-repair scope. Public release is not ready.

The [test audit and replacement ledgers](testing/linux-amethyst-test-audit-2026-10-14/index.html) retain the earlier run history. No live profile changes, manager shutdown, upstream merge, installation or public release are authorized by this document alone.

## 1. Outcome and established decisions

Deliver the full original houseCARL data-layer capability against Amethyst on Linux, including patch authoring, existing-patch iteration, explicitly consented in-place edits, asset writes, Papyrus compile/decompile and deployment verification. Record coverage and validation remain generated from Mutagen; do not introduce record-specific maps or replace missing capability with a limited public release.

The first supported Linux acceptance environment is native Steam with Skyrim Special Edition. Support both physical deployment and Amethyst's current virtual shadow-tree deployment. Preserve upstream Windows behavior and tests. Other launchers, Flatpak and legacy VFS backends are unverified unless separately tested; report their limitations explicitly.

The user has selected these behaviors:

| Decision | Required behavior |
|---|---|
| Upstream | Integrate the reviewed newer upstream before completing the adapter. |
| Windows utilities | Reuse Amethyst's selected existing Proton/Wine environment where needed. |
| Existing disabled patches | Allow repeated edits to tracked houseCARL patches before Amethyst Refresh. |
| Recovery copies | Retain until deployment is verified; no age-based deletion. |
| Partial success | Keep completed items when upstream's operation supports partial success. |
| Symlink visibility | Permit an immediately game-visible staging edit only with explicit per-change consent. |
| Amethyst shutdown | Notify the user before requesting normal closure and explain when restart is safe. Never force termination. |
| Game state | Do not enable, reorder, regroup or deploy mods automatically. |

Proposed Papyrus default, awaiting any correction to the question asked on 14 October: use native Caprica for Linux compilation after the acceptance tests below; retain the Creation Kit compiler as an explicitly selectable backend through Proton. Keep the built-in decompiler as default, with Champollion explicitly selectable. Never silently switch backends after a failed operation.

Implementation takes place in the existing `codex/amethyst-linux-integration` worktree. Preserve the primary checkout's modified AGENTS.md and untracked .agents tree. Changes are separate focused commits. Independent review and explicit user authorization precede merging or public publication.

## 2. Baseline and dependency pins

| Component | Pin or requirement |
|---|---|
| Development starting point | `b9c3071df0784d311f442eec1a10f694d30b2131` |
| Integrated upstream before this plan | `b77ba4b202456affe30dd51111972529e469c278` |
| Upstream integration target | `3570d3fd97654886b32e9e3c90de74dd1158d011` |
| Amethyst primary acceptance | 2.5.5-beta.1, `002a6eeb30f013d38d6a2c69e6f836418b76ed0d` |
| Amethyst stable compatibility | 2.5.4, `edde06e518c0a3ca5ba47a8bbf065c4ecc4a8f92` |
| Filegraph contract | native API 13, schema 9, engine 1, rules 13 |
| Caprica Linux release | `1.0.0-linux`, source `e03d73bf39b7cd0c3db925d8ec8bcef27754fba6` on prepare-app-image; executable version 0.3.0 |
| Champollion Linux release | `1.0.0-linux`, source `5cd9082189b1a60f9d8a292a9071bca74611bd69`; executable version 1.3.2 |
| Runtime | .NET 9 self-contained, trimming disabled; Python 3.12+ for the native snapshot helper |
| Candidate fork version | `2.0.4-amethyst.1`, provided it remains unused when implementation begins |

Do not silently advance these pins while implementing. A changed dependency requires a focused compatibility review and updated evidence. Caprica's default branch is not the release branch; do not substitute it.

The upstream delta inspected for this plan contains 31 commits. Important overlapping areas are typed record indexes, provider resolution, write-engine type lookup, GMST duplicate-name diagnostics, and scan notes across output formats. Integrate those behaviors while retaining the Amethyst saved-snapshot adapter. Build after a clean when changing the generator. Verify generated coverage rather than replacing reflection with compatibility switches.

Current evidence is limited: the development release build and focused 270-test selection passed in the preceding work; 46 Amethyst-focused tests passed. An earlier 123-test placement selection still had 11 failures associated with inherited Windows assumptions. These results do not establish a green full Linux suite. The installed MCP remains the older read-only build and is not the development binary.

## 3. Snapshot authority and deployment model

### Saved inputs and source identity

Continue to read the selected schema-v1 connection manifest and Amethyst's last complete saved filegraph. Never update, refresh, migrate or repair Amethyst's live database. Capture one consistent SQLite read transaction with WAL support, then use Amethyst's pinned native API against a private database copy. Managed winners come from `snapshot().iter_winners`, never reconstructed from priorities or the SQL winners projection; that projection was empirically incomplete.

Capture and validate the effective profile, game identity, canonical staging roots, profile-specific overrides, manager versions, inventory/profile generations, profile intent hash, selected variants, incomplete operations and every source consumed. Identity is canonical path plus device/inode where available, with content hashes for transaction inputs and outputs. Preserve exact Linux path casing; game-relative destination lookup may be case-insensitive, but host paths and consent keys must not be globally lowercased.

Source changes invalidate a result unless they are exact, enrolled outputs of the current transaction or a previously tracked patch explicitly selected as a write target. Never accept unrelated drift merely because the source lies in a houseCARL directory. A tracked pending patch is an explicit target, not an invented active load-order winner.

A tracked patch may be reopened only when its receipt, owner metadata, canonical staging location and current content hashes agree. Derived masters still have to resolve against the valid snapshot or explicitly tracked dependencies. Changed external masters invalidate preparation. Ambiguous plugin basenames require an explicit mod selection. A reconnect or profile switch cannot reassign ownership of a transaction.

### Physical and virtual layouts

Replace the blanket requirement for Data_Core during active deployment with mode-specific rules:

| Layout | Vanilla source | Verification target |
|---|---|---|
| Physical deployment | Data_Core where Amethyst has separated vanilla; undeployed Data only when its state proves it is unmerged | Actual game destination recorded by deployment |
| Current shadow-tree VFS | Original vanilla game tree, validated against the VFS manifest and saved deployment | Validated `.amethyst-vfs/view` destination |
| Unknown or legacy VFS backend | No guessed fallback | Explain unsupported backend and refuse dependent operations |

Validate the VFS manifest version, backend, profile, game/data roots, fixed view paths, upper directories, containment and pending state. The VFS manifest has no generation field: verification must join it to the native committed deployment evidence. Do not hash a logical game path when the physical runtime view is elsewhere.

Observe the actual deployed link mode per entry, including copy fallbacks and per-mod overrides. Atomic replacement of a staging hardlink leaves the old deployed inode until redeployment; a deployed symlink can expose the new content immediately. Determine this from actual destinations and identities, not the global deployment preference.

Before an immediately visible edit, require the selected game to be closed as well as obtaining per-call visibility consent. Check the selected game's identifiable processes. If process ownership cannot be established reliably, report that uncertainty and require the user to close the game before retrying; do not infer safety from the absence of one executable name. These checks also apply to recovery, resume and rollback that would change immediately visible bytes.

### Complete vanilla assets and Papyrus sources

Extend asset enumeration to the complete relevant universe: managed loose winners, managed archive members, vanilla loose files and active vanilla archive members. Include correct engine archive ordering without recreating Amethyst's managed winner algorithm. Resolve archive-container overrides before opening a vanilla archive with the same basename. Preserve native archive namespaces and provider identities.

Read effective profile INIs from `profiles/<name>/ini files` when profile INIs are selected; otherwise derive the selected prefix's My Games INIs without creating them. Capture their hashes and distinguish saved intended inputs from deployed state. Include Skyrim.ccc and native/implicit plugin activation evidence. Do not assume an absent plugin-list row means an official master is inactive.

Malformed or unreadable archives, ambiguous case collisions and missing required sources must produce named coverage gaps. A record-only read can remain available with an appropriate asset-coverage note; an asset-dependent operation cannot treat incomplete enumeration as proof that an asset is absent.

## 4. Coordinated writes and durable recovery

### Lock and shutdown protocol

Use Amethyst's existing `filegraph.lock` with the same Linux advisory-lock semantics as its native catalog. Do not instantiate the live catalog to obtain it, because initialization can write the database. Open the existing lock, acquire exclusive ownership, validate its identity, and retain it through commit or recovery. A missing or unsupported locking contract requires an actionable refusal, not creation of a substitute lock.

Acquire a houseCARL lock for each canonical staging library before the manager lock; sort multiple library locks by canonical identity to avoid deadlock. This serializes different houseCARL processes sharing a library. Lock timeout is an error, including in user-config persistence; never proceed unlocked.

If Amethyst holds its lock, return a typed `manager_close_required` result before preparing managed changes. The conversational workflow announces why closure is needed and requests normal GUI closure where the desktop supports it. The user's prior authorization covers that normal close request. If normal closure cannot be requested programmatically, ask the user to close it manually. Do not kill processes or remove a lock file. Amethyst may prompt about restoration or busy operations; leave that decision visible to the user and stop until closure completes.

Acquire the lock and capture a fresh snapshot after closure; shutdown may have restored deployments or persisted new state. External compilation or other expensive preparation can occur before this lock against captured inputs, but all inputs must be revalidated under lock before publishing. Dry runs and ordinary reads do not require closing Amethyst.

Return `safe_to_restart_amethyst=true` only after all completed units are durably recorded, the incomplete unit is recovered or isolated, and locks are released. An unresolved partial dependency group returns false with a recovery action. Restart safety does not mean safe to enable, deploy or launch the game. Never start the game or restart Amethyst automatically.

### Transaction storage

Add a core transaction coordinator used by all file-producing tools, rather than separate rollback implementations. Put durable state under `${XDG_STATE_HOME:-~/.local/state}/housecarl-amethyst/transactions/<uuid>`. Runtime configuration remains with the existing XDG config owner and preserves unknown fields. State storage is independent of the installed program directory.

Each transaction stores a schema-versioned journal, prepared payloads, recovery copies and a receipt. Use private directories and owner-only access. Stream hashes and copies; do not load entire plugins, archives or backups into memory. Backups must be independent copies or verified copy-on-write clones, not hardlinks to mutable originals. Before publishing any output, flush its prepared data and the journal and sync the relevant directory where supported. Filesystem durability failures abort before publication when possible and otherwise produce recovery-required state.

Required journal fields:

| Group | Fields |
|---|---|
| Identity | schema version, UUID, operation/tool, UTC creation/update times, normalized request digest |
| Context | game/profile identifiers, manifest identity, canonical library identity, snapshot generations and contract versions |
| Inputs | consumed source identities and SHA-256 hashes, consent scope, selected tool/backend versions |
| Units | ordered unit IDs, dependency IDs, source record/remap information needed to resume, unit status |
| Artifacts | canonical destination, role, original existence/identity/hash, prepared hash/size, backup/payload relative path, committed identity/hash |
| Visibility | affected known deployments, actual link modes, immediate-visibility consent, pending/verified deployment evidence |
| Result | successful/failed/skipped units, diagnostics, recovery action, restart-safety result |

No raw credentials, inherited environment dumps or source-script bodies in receipts. Prepared payloads and backups remain private artifacts. Unknown journal schema fails closed for mutation without deleting it. Process-start inspection reports unfinished transactions; it does not automatically undo user data.

### Publication and ownership

Preflight every destination and consent before its first write. Reject traversal, invalid names, symlink escapes, duplicate/case-colliding destinations and aliases into protected game, catalog, prefix or foreign staging paths. Anchor writes to validated directory identities; use no-follow operations and descriptor-relative checks where needed. Explicit output paths do not bypass staging ownership or in-place consent.

Use unique temporary names per transaction. For plugin serialization, preserve the intended plugin basename inside a unique private directory so Mutagen's ModKey remains correct. Never delete a pre-existing fixed `.housecarl-tmp` directory or `.houseCARL-tmp` file as a convenience.

Publish new destinations with exclusive/no-replace semantics. For replacement, recheck original identity and hash immediately before replacement and validate the published file afterwards. The manager lock excludes cooperating Amethyst writes, not arbitrary editors: document this limit and refuse observed conflicts. Do not claim an OS-level compare-and-swap guarantee against an uncooperative same-user writer.

Route metadata, plugin serializers, external-tool results, BSA extraction and loose asset writes through the same enrolled artifact layer. Do not intercept unrelated configuration writes merely because a transaction context is ambient. Plugin plus owner metadata and required paired assets form one logical unit. Multi-file atomic visibility is not promised; the journal provides recovery while the manager is closed.

### States and partial success

Unit states are `prepared`, `committing`, `completed`, `failed`, `rolled_back`, `recovery_required`. Transaction states are `preparing`, `prepared`, `committing`, `completed`, `pending_deployment`, `partial`, `recovery_required`, `verified`, `rolled_back`, `aborted`. A wholly external export ends at `completed` with pending_deployment=false after its output and source-snapshot validation; that is artifact completion, not deployment verification. It needs no manager shutdown unless it also mutates managed staging. Fully managed commits end at pending_deployment until verification succeeds.

| Situation | Required resolution |
|---|---|
| Failure before publication | Remove only owned temporary artifacts; leave original destinations untouched. |
| Incomplete unit, destination still original | Abort that unit; preserve completed units. |
| Incomplete unit, destination exactly matches recorded new bytes | Restore its original bytes or delete its newly created file using ownership checks; retain evidence until recovery completes. |
| Destination matches neither old nor new bytes | Preserve it, mark recovery_required, identify the external conflict. |
| Later item fails after earlier items completed | Keep completed items, return a partial result with exact per-item outcomes. |
| Response lost after commit | Expose durable receipt through status; do not automatically replay a write. |
| Unknown or corrupt journal | Preserve all evidence and refuse further conflicting writes. |
| Recovery-copy allocation or disk-full failure | Abort before publication, or recover the current unit and report any unresolved recovery. |

One plugin operation batch remains atomic as a logical unit. `place` and other upstream per-item operations keep their established successful items. Compact/repoint operations need dependency-aware recovery: preserve the exact FormID remap and all prepared dependent payloads. After a partial failure, block conflicting writes and do not declare deployment readiness until referencers and required assets are consistent. Resume only the recorded remaining units after input validation; never compact the already rewritten plugin again to reconstruct a remap.

Ordinary rollback refuses an output that changed externally or a transaction superseded by a later dependent transaction. Resolve dependent transactions in reverse order. Removing staged bytes that have already been deployed still requires subsequent Amethyst redeployment; rollback does not silently edit the game tree.

### Retention and verification

Retain recovery copies while pending, disabled, shadowed, partial or unverified. Once fresh evidence verifies deployment, backups for the verified transaction may be removed; retain the compact receipt. Superseded edits to the same patch form a chain: verification of the final expected content can close earlier receipts only when the complete chain and all required artifacts are accounted for. Do not wait for an intermediate version that intentionally will never be deployed.

Verification must establish the same game/library/profile scope, a refreshed saved graph containing the output, no relevant incomplete manager operations, committed native deployed entries pointing at the intended source, matching source and deployed-content hashes, correct destination/link mode and required plugin enablement. Use actual VFS view paths in virtual mode. A source timestamp or manifest timestamp is insufficient.

For shared libraries, enumerate known affected deployments; do not delete recovery copies while a known dependent deployment remains unresolved. An unreachable or retired profile needs explicit resolution, not guessed verification. Offline deployment verification is separate from in-game behavior testing.

## 5. Public interfaces and writer parity

Retain existing MCP tool names, record operations, reflection schemas and default output shapes. Add fields without dropping existing scan notes, structured outputs or artifact manifests. Convert internal success/error handling to typed outcomes; string-prefix scanning cannot identify partial writes reliably.

Add `confirm_amethyst_redeploy=false` to managed mutation entrypoints that update existing content. Add `confirm_live_deployment_change=false` for calls capable of editing an existing staging target whose content is immediately visible through a deployed symlink. Both are explicit per-call acknowledgements. Existing persistent per-plugin/per-file consent remains required where upstream requires it; these flags do not replace it. Reject unknown consent paths or ambiguous migrations from older lowercased Linux keys.

Add a generic `housecarl_staging_status(transaction_id?)` read-only tool and `housecarl_staging_manage(transaction_id, action, confirm_amethyst_redeploy=false, confirm_live_deployment_change=false)` mutation tool. Actions are `verify`, `recover`, `resume`, `rollback`. A transaction ID is mandatory for management; there is no wildcard mutation. Verification may update houseCARL receipts/retention but never Amethyst state. Invalid transitions return allowed next actions. A never-mutating status tool remains safe for inspection while Amethyst is open. The existing load-order status includes a bounded pending-transaction summary.

The additive `amethyst_staging` result carries `transaction_id`, `state`, `pending_deployment`, `immediate_visibility`, `safe_to_restart_amethyst`, `items`, `diagnostics`, and `next_actions`. Each item identifies its unit and destination, outcome and resulting hash when committed. Paths can be private local output but must be scrubbed from published diagnostics. Failure or partial failure sets MCP `isError=true` while preserving all successful item evidence. Mark partial dependent groups explicitly unsafe to deploy. Large results retain these summaries in both structured content and the first manifest record.

| Surface | Amethyst implementation requirement |
|---|---|
| apply/create/forward/remove | New staging, owned `into`, and sanctioned in-place targets; retain dry runs, touched-record readback and generic validation. |
| create_plugin | New header-only plugin; preserve upstream's empty-plugin master behavior. |
| copy | New/into targets, copied closure and link checks; accurate associated-asset reports. |
| compact_plugin | Existing explicit consent contract plus staging/deployment guards; preflight target and all repoints, preserve remap for recovery. |
| merge_plugins | New target, collision/remap handling and linked asset/SEQ consistency. |
| nif_set | New/into/in-place as upstream supports; retain file consent and validate actual on-disk output. |
| place | Per-item outcomes; successful placements survive unrelated failures; required paired assets recover together. |
| write_seq | New/into or valid explicit output; identical bytes are a no-op, without inventing ownership. |
| compile_script | Native/CK backend behind existing tool; private outputs validated before publication. |
| decompile_script | Built-in/Champollion behind existing tool; never overwrite an existing PSC by default. |
| bsa_extract/repack | Enroll actual file-producing paths; extract traversal checks; repack never silently replaces an existing archive. |

New mods remain absent from active winners until Amethyst records them. No tool writes modlist.txt, category dividers, priorities, plugins.txt, loadorder.txt or catalog records to make a patch appear installed. Metadata must accurately describe staging/pending state instead of promising that it wins in-game. Masters remain derived from actual references under upstream rules, never manually supplied.

Fix the filter that blocks `housecarl_nexus_check_updates` in saved-snapshot mode despite the tool taking explicit mod IDs and not requiring the load-order service. Preserve authentication isolation. Implement Amethyst's offline installed-version reader from its actual metadata fields and file-ID/version rules rather than treating it as MO2 metadata. Preserve Nexus game-domain identity; this project's operations stay Skyrim SE scoped.

## 6. Papyrus and external utilities

### Verified Linux tool findings

The Styyx1 releases provide AppImages. Both were downloaded to a temporary directory, verified against their published SHA-256 hashes, extracted without FUSE and invoked successfully on the current machine. Caprica's help path exits 255 despite printing valid help; do not use a zero help exit as the only availability probe.

| Release asset | SHA-256 |
|---|---|
| caprica-0.3.0.AppImage | `0f6f448df34567d597b6f2d5f76330aebf61ef039f7855b8087a426e42eb2653` |
| Champollion-x86_64.AppImage | `80961763e5959347c78f50f66153b5e6797041c87a166268fee0de0912b4087c` |

A temporary pure-arithmetic Skyrim script containing a loop and branch compiled to a Skyrim PEX, decompiled through Champollion and recompiled successfully. An invalid PSC failed with no output. An invalid PEX produced an error and no output, but Champollion exited zero. The first positive probe lacked the Hidden flag required by Caprica's internal Skyrim base script and failed correctly; adding a minimal probe-only flag definition allowed compilation. Production must use the actual selected Skyrim flag definitions.

This is a smoke test, not gameplay equivalence, full mod compatibility or cross-distribution proof. The publishers describe CachyOS builds and do not establish broad distro portability. The public package must build and test pinned native executables on the supported distribution baseline rather than assume these AppImages run everywhere.

### Compiler contract

Add optional `compiler_backend` to `housecarl_compile_script`, values `caprica` and `creation-kit`. An explicitly configured legacy CK compiler remains authoritative when the new parameter is omitted. A fresh Linux installation selects bundled Caprica after its acceptance gate; Windows retains CK by default. Return selected backend, executable version and target game in diagnostics. A missing chosen backend gives setup instructions, never an automatic fallback.

Caprica invocation uses an argument list, an isolated working directory, an explicitly supplied private config file, `--game=skyrim`, `--ignorecwd`, `--enable-language-extensions=false`, explicit flags and output paths. Keep optional optimization disabled and do not force unsupported Skyrim optimization. Retain Skyrim compatibility defaults and do not enable release/final stripping or stricter language semantics silently. Do not accept arbitrary user CLI arguments that can redirect output or change the game target.

The adapter validates the real flags file before execution. The inspected Caprica source can fall back to Starfield flags when a file cannot be resolved; this fallback must never be reached unnoticed. Capture source, flags, import selection, backend and invocation settings in provenance. Missing source dependencies are errors, not license to synthesize declarations from decompiled bodies or signature documentation.

Resolve script-name providers once using the selected snapshot, with source-folder/explicit imports first, applicable managed providers next and vanilla last. Respect both recognized source layouts. Materialize the required source closure into private import directories so Linux filename casing does not change Papyrus's case-insensitive symbol semantics. Refuse ambiguous providers at the same precedence; preserve explicit override precedence and report it. Reject path separators or names that would escape the materialized tree. A truncated or unreadable dependency walk is a named diagnostic and cannot imply complete dependency validation.

Success requires this invocation to create the expected PEX in an initially empty private output directory. For Caprica, require a successful compile exit and no fatal diagnostics, then parse the output and confirm Skyrim format, script identity and expected object count. CK has different exit-code semantics, so its adapter requires valid fresh output and no fatal compile diagnostics rather than blindly sharing Caprica's rule. Neither backend relies on a prior file's modification time. Publish only validated bytes through the transaction coordinator, leaving the prior good output intact on failure.

### Decompiler contract

Add optional `decompiler_backend=builtin|champollion`, default builtin. Keep the built-in fail-loud behavior for unsupported control flow, hierarchy gaps and unavailable parameter defaults. No successful decompilation promises recovery of comments, original formatting or defaults that PEX does not retain.

Champollion runs against one validated input PEX in a private directory with explicit `--psc` output. Do not enable recursive discovery, source-tree recreation or threading for a single tool item. Prevalidate Skyrim format and script/object names before launching; validate exact output paths afterward. Require expected source output, absence of reported failures and no unsafe extra output. Exit zero alone is insufficient. Incomplete reconstruction is explicitly marked and cannot be auto-published as a verified replacement script.

Do not silently replace a built-in result with Champollion output or claim identical semantics merely because recompilation succeeds. Provide backend provenance and preserve warnings. Differential fixtures cover both tools, including unsupported patterns and missing metadata. Keep Champollion as a separate process; do not link its implementation into the C# server.

Caprica uses MIT licensing; Champollion uses LGPL-3.0. Package required notices and corresponding source/build material for redistributed components and their dependencies. Distribution compliance is a release checklist item, not an assumption that a subprocess removes all obligations. Do not redistribute Bethesda or third-party mod scripts without the necessary rights.

### Proton and BSArch

For CK and BSArch, read Amethyst's selected profile prefix, effective game path and Steam compatibility-tool assignment. Reuse the existing prefix and runner. Do not call Amethyst helpers that create prefixes, modify registry values, install UMU or initialize tools merely to discover an environment. No implicit prefix creation or arbitrary Proton fallback.

Build the runner's argument vector without a shell. Derive Wine paths from the selected prefix's dosdevices mapping; do not assume Z always maps the host root. Validate translation and handle spaces, Unicode and special characters. Reject unsupported delimiter-bearing import paths with a precise error if a backend's grammar cannot represent them.

Use private input/output workspaces and bounded captured diagnostics. Default utility timeout is 120 seconds, configurable up to 900 seconds; cancellation terminates only the owned child process group and drains pipes with a bounded wait. Never run wineserver-k or kill the game. Wine itself may update its existing prefix during normal tool execution; the adapter must not deliberately initialize a prefix or reconfigure its registry. Missing runtime dependencies are actionable setup failures; no silent installs. Validate PEX/BSA content before atomic publication. Preserve BSArch archive naming and load-trigger semantics.

## 7. Implementation packages for Luna

Complete these in order. Each package has its own tests and focused commits; do not defer all safety work to the end.

| Package | Implementation | Exit evidence |
|---|---|---|
| 1. Upstream integration | Integrate the pinned upstream into the existing worktree; preserve adapter and generated coverage; record conflict resolutions. | Clean generator build, baseline comparison and upstream schema/output tests. |
| 2. Snapshot completeness | Mode-aware vanilla roots, VFS validation, vanilla archive enumeration, effective INIs, strict paths and owned pending-target reads. | Physical and virtual saved-state fixtures, native winner agreement and asset coverage tests. |
| 3. Transactions | Shared-library and manager locks, journal, publication, consent, recovery, partial outcomes, typed status/manage tools. | Failure injection at every state transition and independent-process concurrency tests. |
| 4. Record writers | Route all record write lanes through transactions; into/in-place, compact/repoint/merge and metadata. | Full upstream write matrix plus Amethyst staging/roundtrip tests, no hand-mapped types. |
| 5. Assets and exports | NIF, placement, SEQ, BSA, external exports and safe cleanup. | On-disk format/readback tests, traversal and partial-result tests. |
| 6. Papyrus and runners | Caprica, Champollion, captured import materialization, and native-Steam selected-runner resolution implemented; CK/BSArch use the selected Proton prefix and dosdevices path mapping. | Focused Package 6 selection: 38 passed; full solution build passes. Real selected-prefix execution acceptance remains outstanding; Flatpak/custom Steam roots are unsupported. |
| 7. Deployment and status | Receipt verification, superseded chains, shared profiles, retention and update metadata fix. | Hardlink/copy/symlink/VFS verification fixtures and no false verified states. |
| 8. Packaging | Linux installer/update/rollback, reproducible dependency builds, manifests/notices, host registration, docs and release checks. | Fresh offline package install, upgrade/rollback and uninstallation tests on clean supported hosts. |
| 9. Acceptance and review | Full Linux/Windows checks, isolated full workflow, separately authorized real-profile checks and independent review. | Review findings resolved and explicit public-release decision. |

Likely integration seams are the existing Amethyst snapshot/filter/staging classes, core atomic-file and writer layers, LoadOrderService provider resolution, compiler/decompiler tools and installer. Introduce a core transaction abstraction rather than placing filesystem recovery policy inside every MCP endpoint. Core must not depend on MCP types. The manager adapter supplies snapshot, lock, enrollment and verification services; MO2 remains outside Amethyst-specific acknowledgements.

## 8. Acceptance matrix

Tests should exercise observable failures and contracts, not repeat the implementation. Use synthetic redistributable records/scripts and isolated manager catalogs wherever possible. No live profile is a test fixture by default.

| Area | Mandatory scenarios |
|---|---|
| Generated coverage | Every modeled record type remains exposed; generation clean-build check; unknown upstream type gap is loud; no new per-type translations. |
| Snapshot | WAL changes; stale hash/generation; mismatched native version; DB replacement; profile switch; missing source; four-plugin SQL-projection regression; helper failure. |
| Paths | Mixed case, Unicode, spaces, long names, `..`, absolute archive entries, symlink escape/swap, case collisions, shared-library aliases, wrong root identity. |
| Deployment layouts | Physical undeployed/deployed, missing Data_Core where unsafe, VFS vanilla Data without Data_Core, shadow manifest mismatch, legacy/unknown backend, pending operation. |
| Locking | Amethyst open; graceful close refused/cancelled; close restores deployment; two houseCARL processes; lock timeout; lock inode changed; no automatic force kill. |
| Mutation units | New and existing targets, source originals untouched by default, touched-record readback, localized/reserved-ID refusals, expected masters, empty plugin semantics. |
| Transactions | Kill/fault before and after every journal/publish step, disk-full/permission failures, interrupted backup, corrupt journal, rollback external-third-hash conflict, lost response. |
| Partial operations | Earlier success survives later failure; same-plugin batch remains consistent; multi-file unit failure; compact/repoint partial group resumes original remap only. |
| Repeated patch edits | Edit disabled output twice without Refresh, change externally between calls, ambiguous same basename, pending master dependency, superseded receipt verification. |
| Visibility | Hardlink inode replacement stays pending; actual copy fallback; symlink refuses without per-call consent; VFS actual view hashed; game-running check before immediately visible writes. |
| Retention | Disabled/shadowed outputs keep backups; correct deployed hash releases eligible backups; stale generation/disabled ESP/wrong source cannot verify; shared profile unresolved. |
| Papyrus providers | Both source layouts, explicit overrides, transitive imports, missing dependencies, mixed-case script names, source drift during compile, real Skyrim flags, unrelated ambient caprica.cfg ignored. |
| Compiler | Wrong target prevented; valid branch/loop/state/property/array/native-call fixtures; malformed source; missing flags; no old-output false success; timeout and compiler crash. |
| Decompiler | Built-in unsupported-flow warning, optimizer patterns, invalid/multi-object PEX, unsafe object names, Champollion zero-exit failure, output collision, non-equivalent reconstruction warnings. |
| External runners | Exact Amethyst prefix/runner selection, unavailable mapping, path translation, no shell expansion, bounded stdout/stderr, child-only cancellation, no deliberate prefix initialization or registry reconfiguration. |
| Installation | Fresh install, existing config preservation, payload hash mismatch, unsafe package paths, interrupted update, rollback with pending receipts, uninstall retaining user data. |
| Output | Text/JSON/structured/manifest agreement; notes preserved; partial isError with successes visible; manager mode and deployment state truthful; no credentials leaked. |

The user-selected acceptance scope is Linux/Amethyst; its full relevant suite must be green. Windows execution remains unverified. Replace inherited Windows-only fixture assumptions with meaningful Linux assertions where their intent applies. A narrowly documented skip is acceptable only for genuinely unavailable external tooling or OS-specific behavior, not for a required broken Linux feature. Classify inherited compiler warnings and prevent new unexplained warnings.

Native tools must be exercised on the minimum supported Linux package baseline and the user's environment. Add fixtures with real source-dependency patterns using redistributable sources; the current arithmetic smoke test is insufficient. Where gameplay is needed to establish script behavior, report it as pending until an explicitly authorized test is completed. No compile success or disk verification is reported as in-game testing.

## 9. Packaging and release gates

Produce a Linux x64 self-contained untrimmed server, matching generated references and current skill bundle, installer, pinned native filegraph helper/module, native Papyrus utilities, checksums and third-party notices. Build native components reproducibly from pinned sources on a declared baseline; use Ubuntu 24.04 x64 as the initial CI/package baseline and test the user's Arch-derived environment separately. Python 3.12+ remains an explicit system prerequisite with a clear installer check. Verify native module ABI and shared-library dependencies rather than copying an unversioned AppImage mount into a release.

Reuse useful Linux installer logic from the older fork selectively; do not overwrite the newer upstream installer or skill inventory wholesale. Installation must validate the entire manifest, preserve unrelated Codex/Claude registrations and unknown settings, and stage program updates before atomic activation. Check for a running server before replacement. Keep credentials and journals outside the install tree. Downgrades that cannot interpret pending journals refuse before switching binaries. Uninstall removes program files and owned host registration, retaining configuration and recovery data unless separately requested.

Update fork identity, help text and URLs while keeping the `housecarl_` MCP prefix and upstream attribution. Eliminate promises of immediate game visibility from staging results. Document Refresh, enablement and deployment as user-controlled actions. Add release notes covering saved-state semantics, transaction recovery, supported deployment modes and external-tool setup.

The public-release gates are:

1. Complete upstream writer parity through the guarded Amethyst layer, including into/in-place and asset-producing tools.
2. Recovery, partial success, repeated edits and actual deployment verification pass the failure matrix.
3. Complete saved-source/vanilla asset access works in both accepted deployment modes.
4. Native compiler/decompiler and required Windows utilities have truthful, validated behavior.
5. Clean installation/update/rollback works from the actual release archive, with no private paths, secrets or unlicensed source bundles.
6. Full test evidence and an independently reviewed PR are available; real-profile acceptance uses an explicitly approved isolated backup and leaves the live profile untouched unless separately authorized.
7. The user authorizes public push/release after reviewing the concrete candidate and outstanding limitations.

## 10. Evidence links

- [HouseCARL upstream integration target](https://github.com/Avick3110/houseCARL/commit/3570d3fd97654886b32e9e3c90de74dd1158d011)
- [Amethyst beta source](https://github.com/ChrisDKN/Amethyst-Mod-Manager/tree/002a6eeb30f013d38d6a2c69e6f836418b76ed0d)
- [Caprica Linux release and portability note](https://github.com/Styyx1/Caprica/releases/tag/1.0.0-linux)
- [Caprica released option parsing and defaults](https://github.com/Styyx1/Caprica/blob/e03d73bf39b7cd0c3db925d8ec8bcef27754fba6/caprica/main_options.cpp)
- [Caprica released native build recipe](https://github.com/Styyx1/Caprica/blob/e03d73bf39b7cd0c3db925d8ec8bcef27754fba6/xmake.lua)
- [Champollion Linux release](https://github.com/Styyx1/Champollion/releases/tag/1.0.0-linux)
- [Champollion CLI and error handling](https://github.com/Styyx1/Champollion/blob/5cd9082189b1a60f9d8a292a9071bca74611bd69/Champollion/main.cpp)

Local temporary probe files were created under `/tmp/housecarl-papyrus-probe-fitexmnc`; they are disposable evidence, not dependencies of the implementation. Reproduce the probes in permanent isolated tests during package 6.
