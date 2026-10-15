# houseCARL 2.0.5 integration and Linux package checks

The [v2.0.5 Linux preview](https://github.com/ni1by2/houseCARL-Amethyst/releases/tag/v2.0.5) was published as a prerelease on 15 October 2026 from `87e9268dc1052e31343fc8ff5bc014bd53b6c264`, after [PR #10](https://github.com/ni1by2/houseCARL-Amethyst/pull/10) and [PR #11](https://github.com/ni1by2/houseCARL-Amethyst/pull/11). The final verification below supplements the original candidate results; earlier installation/rollback checks were not repeated after the Proton resolver change.

## Source revisions

- Fork baseline: `7101d549747aebb15b006942ed5ebe2fa0c35233`.
- Last shared upstream commit: `3570d3fd97654886b32e9e3c90de74dd1158d011`.
- Integrated upstream head: [`4e28505122165a1e210d0fd56d1382b377669421`](https://github.com/Avick3110/houseCARL/commit/4e28505122165a1e210d0fd56d1382b377669421), the 2.0.5 release head.

The fork already contained most 2.0.5 work. This integration adds the remaining 15 commits: schema-based filter planning, generated condition parameters, reflection handling, tests, references and release metadata. The filter engine and new schema tests match upstream. The generator retains the fork's internal visibility; the write engine retains the Amethyst transaction commit hook and unique temporary directories.

The merge conflicts were in `read-engine.md`, `select-and-walk.md` and the plugin manifest. Documentation keeps the fork's wording while describing the new behaviour. The manifest retains the Amethyst identity and advances to 2.0.5. User README edits remain, with one added link to runtime prerequisites. No adapter refactor is part of this change.

## Test changes inherited from upstream

`SchemaFirstPathTests` and `SchemaFirstPathFoldTests` cover invalid paths, valid link and scalar members, containment, raw-byte indexes, condition parameters and mixed-type filters. `SchemaWalkAgreementTests` checks generated-schema coverage against the members the runtime reader resolves.

Existing predicate tests now assert planning refusals before records are read. For example, `ARememberedVerdictNamesItsOwnNote` becomes `ALinkSideBadPathIsRefusedBeforeAnyTargetIsRead`: invalid paths no longer reach the link-verdict cache. List-key and quantifier assertions likewise move to the planner. These are changes to the tested contract, not platform skips. The previous Linux test-removal audit is unchanged.

## Earlier candidate verification on 15 October 2026

| Check | Result |
|---|---|
| Clean Release solution build, .NET SDK 9.0.315 | Passed; compiler/analyser warnings remain |
| Full Linux/Amethyst C# suite | 7,211 passed, 0 failed, 1 skipped |
| New schema-planning tests | 57 passed |
| New schema/reader agreement tests | 4 passed |
| Amethyst-named cases in the full suite | 105 passed |
| Real Caprica end-to-end cases | All 5 passed |
| Python installer/package suite | All 21 passed |
| Ubuntu 24.04 self-contained publish and package | Passed using the previously verified Ubuntu/glibc 2.39 native-tool build |
| Extracted Ubuntu archive | MCP initialize and tools/list passed; version 2.0.5, 34 tools |
| Fresh Ubuntu 24.04 container, documented runtime libraries, no .NET SDK | Install, installed-payload hash checks, native API 13/schema 9 import, MCP startup and uninstall passed |
| Actual candidate upgrade from the earlier disposable 2.0.4 installation | Upgrade, rollback to the previous activation, return to 2.0.5, retained state and 2.0.5 startup passed |
| Same Ubuntu-built archive on CachyOS, glibc 2.44/Python 3.14 | Native module import and MCP initialize/tools/list passed |

The single skipped test is `FaceGenWorldDisposeTests.AFileHeldOpenFailsTheAssertNamingTheFolderAndTheWorldIsDeletedOnceReleased`. It asserts Windows file-sharing deletion behaviour; Linux allows deleting an open file. Windows execution was not performed.

## Package corrections

The earlier archive contained the Linux apphost but omitted the .NET assemblies and runtime files. Starting it failed because `housecarl-mcp.dll` was absent. The new archive probe reproduced that failure. Packaging now copies the complete published server directory and refuses a publish missing key runtime files. Linux publication also stamps the version from the plugin manifest; the probe checks the MCP version against the archive manifest.

CI builds the actual archive and runs the same MCP probe after its source and installer tests. The probe uses temporary configuration, checks the native filegraph API/schema, and never connects to a game profile or registers an MCP host. Its optional installed-prefix mode verifies an installation against the candidate manifest before starting it.

A bare Ubuntu image also exposed a missing ICU prerequisite. Installation guidance now lists the OS runtime libraries, including `libicu74`. The clean-container acceptance run used those packages and Python without a .NET SDK. The installer instructions now use the standalone `install-linux.py` shipped beside the archive.

The rollback test proves activation and state retention. The earlier 2.0.4 test package was the known incomplete archive, so this does not claim that its server became runnable after rollback. The final activation was restored to the working 2.0.5 candidate.

## Final release verification

The follow-up added discovery of explicitly selected system-installed Proton runners, including root-level VDF manifests. Independent GitHub Codex reviews found stale server output in the package build and missing root-level manifests in runner discovery. Both findings were corrected before publication.

| Check | Result |
|---|---|
| [Fixed-head PR CI](https://github.com/ni1by2/houseCARL-Amethyst/actions/runs/38777264531) | 7,218 C# tests passed, 0 failed, 1 Windows-only skip; all 21 Python tests passed; archive startup passed |
| [Final merged-source CI](https://github.com/ni1by2/houseCARL-Amethyst/actions/runs/38779627212) | Passed for `87e9268d`; its tree matched the tested PR head |
| Focused Steam/Proton resolver cases | All 18 passed, including root-level absolute/relative descriptors and symlink escape refusals |
| Final Ubuntu 24.04 package build | Seeded stale output removed; payload hashes, native API 13/schema 9, MCP version 2.0.5 and 34 tools, and archive checksum passed |
| Final archive installed on the Arch-derived host | Isolated home, Claude Code/Codex registration and installed-server startup passed without a host .NET SDK |
| Real selected-Proton utility execution | BSArch pack/list/extract passed under Proton CachyOS with a copied prefix and synthetic inputs; extracted bytes matched the source, including a source path with spaces and Unicode |
| Input preservation | All 14 original synthetic profile/source files unchanged; eight monitored live configuration/prefix files unchanged; outputs remained in disposable staging |
| Publication | Public prerelease, three assets with matching server-side SHA-256 digests, tag at `87e9268d`, and unauthenticated checksum download verified |

The final archive is 64,413,479 bytes with SHA-256 `dc0e7a7aa859943a5ceb461c0c5c0da38fe3802ebb310865e4b1dc0615cab382`. The Ubuntu/glibc 2.39 native utilities were reused from the previously verified pinned build; they were not rebuilt for the Proton-only change.

The isolated acceptance used the installed MCP server, a synthetic catalog created with Amethyst's native producer, copied BSArch/game executables and a copied native Steam prefix. The game was never launched. The selected runner was the system-installed `proton-cachyos-slr` package `1:11.0.20261005-1`. Its actual invocation was verified through an execution trace. A private virtual display supported the headless Wine run; the host and live Skyrim paths were mounted read-only. Only the copied prefix was migrated by Proton. Raw logs, prefix contents and copied executables are private test artifacts and are not release assets.

## Scope and limits

No live Steam mapping, Skyrim profile/prefix, installed mod category/order, host MCP registration, activation or deployment was changed. The test establishes isolated package/registration/staging and selected-Proton archive behavior. It does not establish Creation Kit compiler compatibility with that runner, the actual manager GUI workflow, live-profile deployment or gameplay. Windows execution was not tested. The adapter refactor remains deferred.
