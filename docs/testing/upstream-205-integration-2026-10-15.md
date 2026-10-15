# houseCARL 2.0.5 integration and Linux package checks

## Source revisions

- Fork baseline: `7101d549747aebb15b006942ed5ebe2fa0c35233`.
- Last shared upstream commit: `3570d3fd97654886b32e9e3c90de74dd1158d011`.
- Integrated upstream head: [`4e28505122165a1e210d0fd56d1382b377669421`](https://github.com/Avick3110/houseCARL/commit/4e28505122165a1e210d0fd56d1382b377669421), the 2.0.5 release head.

The fork already contained most 2.0.5 work. This integration adds the remaining 15 commits: schema-based filter planning, generated condition parameters, reflection handling, tests, references and release metadata. The filter engine and new schema tests match upstream. The generator retains the fork's internal visibility; the write engine retains the Amethyst transaction commit hook and unique temporary directories.

The merge conflicts were in `read-engine.md`, `select-and-walk.md` and the plugin manifest. Documentation keeps the fork's wording while describing the new behaviour. The manifest retains the Amethyst identity and advances to 2.0.5. User README edits remain, with one added link to runtime prerequisites. No adapter refactor is part of this change.

## Test changes inherited from upstream

`SchemaFirstPathTests` and `SchemaFirstPathFoldTests` cover invalid paths, valid link and scalar members, containment, raw-byte indexes, condition parameters and mixed-type filters. `SchemaWalkAgreementTests` checks generated-schema coverage against the members the runtime reader resolves.

Existing predicate tests now assert planning refusals before records are read. For example, `ARememberedVerdictNamesItsOwnNote` becomes `ALinkSideBadPathIsRefusedBeforeAnyTargetIsRead`: invalid paths no longer reach the link-verdict cache. List-key and quantifier assertions likewise move to the planner. These are changes to the tested contract, not platform skips. The previous Linux test-removal audit is unchanged.

## Verification on 15 October 2026

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

## Remaining release acceptance

These are local candidate results, not a published release or a hosted CI result for the final commit. Commit and merge the changes, then rebuild the final archive from that committed source and repeat its package check before upload.

Acceptance against the user's actual Amethyst profile, desktop host registration on the Arch-derived system, and a real selected-Proton utility run remain separate. No live Skyrim profile, installed mod category, activation or deployment was changed by this integration. The adapter refactor is deferred until after release.
