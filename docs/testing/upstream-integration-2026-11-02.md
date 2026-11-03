# Upstream integration, 2 November 2026

The integration validated on 2 November adds houseCARL's newer bulk-read implementation and a matching native reader for Amethyst's schema-10/rules-15 snapshots. The published Linux preview at that time was 2.1.2. The implementation has since merged in [PR #20](https://github.com/ni1by2/houseCARL-Amethyst/pull/20); see the [2.1.3 release verification](linux-213-release-2026-11-03.md) for the versioned package. No installed server, host registration, live profile, mod order or deployment was changed.

## Reviewed sources

| Project | Previous source | Reviewed source | Impact |
|---|---|---|---|
| houseCARL | `1bdd0a8d1f34a7cef99a856ac61d0f4fb33cb732` | [`a4dc3ebee6ce2a0b2f8672eb5bb77fcd75206a0f`](https://github.com/Avick3110/houseCARL/compare/1bdd0a8d1f34a7cef99a856ac61d0f4fb33cb732...a4dc3ebee6ce2a0b2f8672eb5bb77fcd75206a0f), main | 38 commits after the released 2.1.2 source; bulk reads, fault handling and traversal bounds are useful to this fork. |
| Amethyst stable | `c25cffff2585a5615433c5dc96d0be47d9a68bb9`, 2.6.0 | [`2ebe92730ccc66ecc81d12c3bc067f022b72708f`](https://github.com/ChrisDKN/Amethyst-Mod-Manager/compare/c25cffff2585a5615433c5dc96d0be47d9a68bb9...2ebe92730ccc66ecc81d12c3bc067f022b72708f), 2.6.1 | Packaging and version changes only; the native/headless adapter inputs are unchanged. |
| Amethyst beta | Same 2.6.0 source | [`7de35eaf0c7191defa7aee33d61452ddf73f7da3`](https://github.com/ChrisDKN/Amethyst-Mod-Manager/compare/c25cffff2585a5615433c5dc96d0be47d9a68bb9...7de35eaf0c7191defa7aee33d61452ddf73f7da3), 2.6.2-beta.3 | Rules revision 15 and root-mod routing changes require a separately pinned reader. |

The fork review base is published 2.1.2 at `7560fd134faea400e1ee5871550b0c13f2fce74d`. Source validation covers engine integration `2662f97c` and native compatibility `15a6d153`; subsequent changes are documentation only. The SDK remains .NET 10.0.403, with self-contained runtime 10.0.14.

## Engine integration

BodyGather becomes the common record-body access path. Failed groups retain their per-key errors, source chains memoize named donors, and closure walks batch one bounded depth at a time. Asset Walk reads template members and races in rounds and stops further reads at its record/template bounds. SkyPatcher post-state warnings follow row order. The corresponding upstream source changes and regression tests are included.

Amethyst's native saved-provider selection, source identity checks and staging transactions remain authoritative. The existing Traits-template-cycle refusal before voice resolution is retained. Coverage still comes from Mutagen's generated schema; no hand-written record maps or job-specific bulk tools were added.

Upstream's new source-watch script, pin inventory and scheduled workflow are omitted from this integration. Its newest commit about Nexus MAIN-file versions changes that monitoring script, not the runtime Nexus client. It is not a fix for mod version columns.

## Amethyst compatibility and API assessment

Amethyst's [root-mod Top Level change](https://github.com/ChrisDKN/Amethyst-Mod-Manager/commit/659925e8de1481e6358a7159c7906d4717dcd818) applies per-mod strip prefixes to root-installed mods. The filegraph schema remains 10 and the native API remains 13, while the rules revision advances from 14 to 15. A producer stamp change alone does not prove an incompatible binary format; the new reader pins the changed producer semantics and prevents an older reader from interpreting newer saved intent.

Development packages now include these exact contracts, all with API 13 and engine revision 1:

| Schema | Rules | Native source |
|---|---|---|
| 9 | 13 | `edde06e518c0a3ca5ba47a8bbf065c4ecc4a8f92` |
| 10 | 14 | `abeae84afce940c6819f80c1cd4e6e51db2e935d` |
| 10 | 15 | `7de35eaf0c7191defa7aee33d61452ddf73f7da3` |

The captured schema/rules pair selects the native module. A status handshake verifies API, schema, engine, rules, inventory generation and readiness before snapshot results are accepted. Wrong modules, unknown revisions and changes between supported contracts during a call refuse. Native initialization continues to affect only a disposable database copy.

A new fixture comes from the pinned beta's actual Python filegraph service and compiled native extension. Its root-installed `RootTop` mod strips `Wrapper` from `Wrapper/Data/Meshes/top.nif`, producing the saved root destination `data/meshes/top.nif`. Tests compare houseCARL's winners, providers and routed source identity against that producer output. Earlier fixtures remain intact.

The headless API adds independent-instance creation and related operations, plus tool and launch improvements. Its public method registry still does not expose a complete atomic export of the saved generation, candidates, selected variants, exclusions, winners, provider chains and exact source identities that houseCARL needs. Existing conflict inspection and revision tokens provide useful subsets. They do not yet replace the captured native snapshot, so an API refactor remains deferred. A future API should supply one generation-bound snapshot or export token and enough identity data to revalidate reads and staged outputs.

New Amethyst instances have separate identities and paths. This change does not claim automatic discovery or acceptance of every new instance layout; connection selection remains explicit. Nexus metadata keys consumed by this fork remain unchanged, so no metadata-reader change was needed.

## Executed verification

- Full Linux C# suite: **7,880 passed, zero failed, one existing Windows-only skip**, 7,881 total.
- Focused compatibility and affected-engine selection: **164 passed**.
- Python installer/package suite: **21 passed**, including refusal of an archive missing the rules-15 module.
- Rules-15 checks cover producer parity, root routing, asset traversal, excluded files, changed sources, mismatched readers, unknown rules and rollback after a profile race.
- Self-contained, untrimmed Linux archive built successfully. Its probe loaded all three native contracts, initialized MCP and found all 34 tools. The probe verifies the actual rules stamp by opening a separate empty catalog with each packaged module.
- Dependency notices match the publish output: **45 components**, with both tracked copies consistent and all three Amethyst source pins documented.
- A fresh independent agent reviewed the implementation diff and reported no actionable findings. The agent inspected source and tests without repeating the build or accessing live state.

Validation ran in disposable containers with synthetic profiles and isolated XDG state. Existing Caprica and Champollion binaries were reused because their sources and pins did not change. No live profile, Proton, deployment or gameplay acceptance was run.

Graphify's refreshed code-only graph has 18,775 nodes and 66,436 edges. Its diagnostics report no dangling endpoints or exact duplicate edges; extraction warns about shared namespace IDs and retains nodes for one source outside the current scan. Documents and unsupported file formats were not semantically indexed. Code Review Graph was refreshed after the source changes with no reported parse failures. Caller hints were checked against source; graph edges do not establish reflection coverage or replace independent review.

## Publication boundary

The private archive built on 2 November reported the source manifest's **2.1.2** label. Its SHA-256 was `ffe43f984ec6bb11644b65486b8e6e28952aa831314cb521a882d69462730af5`. It was never published and must not replace the published 2.1.2 archive. The versioned 2.1.3 package has its own checksum and verification record linked above. Published 2.1.2 includes the earlier two native readers and refuses rules-15 snapshots.

The [integration CI passed](https://github.com/ni1by2/houseCARL-Amethyst/actions/runs/41501409813) at `5dc1d2fa`; the rebased merge at `99f65b9c` has the same tree. Release publication and any local upgrade remain separate from these implementation checks.
