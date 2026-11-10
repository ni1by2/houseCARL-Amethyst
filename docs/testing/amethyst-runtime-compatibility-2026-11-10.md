# Amethyst runtime compatibility verification, 10 November 2026

The candidate extends merged suite PR [#22](https://github.com/ni1by2/houseCARL-Amethyst/pull/22), base `583bb465823c92572fd5cf2ec877b28a61c7c4f5`. It accepts Amethyst 2.6.3 and 2.6.4-beta.1 saved snapshots through explicit producer/reader mappings. It remains unreleased; the published 2.1.3 archive has not changed.

## Producer checks

| Amethyst producer | Captured contract | Runtime mapping | Packaged candidate result |
|---|---|---|---|
| [2.6.3, `788a7126`](https://github.com/ChrisDKN/Amethyst-Mod-Manager/commit/788a71262ca479b6cbccc53ff3a0083250e3833c) | API 13/schema 10/engine 1/rules 18 | Existing native rules-15 reader | Accepted; explicit connection, status, asset winner/provenance, changed-source refusal and changed-intent refusal passed. |
| [2.6.4-beta.1, `8ac86bdb`](https://github.com/ChrisDKN/Amethyst-Mod-Manager/commit/8ac86bdb9bc6f32b5c49eae69890ae96e4f16943) | API 13/schema 10/engine 1/rules 19 | Matching native rules-19 reader | Accepted; the same read/refusal checks passed. |

Each final experiment compared ten winner identities and ten provider chains, generations and uncommitted-deployment verification. Both matching producer controls passed. Inputs retained their data hashes and every report retained `approved:false`.

The rules-19 experiment includes `Beta` and `beta` folders, with `Alpha` between them, all supplying the same plugin-bound archive. The old rules-14/rules-15 readers return wrong winner identities and fail this comparison. The rules-19 reader passes. Consequently its overall comparison report intentionally remains `comparison_failed`, while its matching-reader and packaged-server sections pass. An earlier experiment also demonstrated the rules-18 reader failing this case. These are retained incompatibility findings, not failures to waive. They supersede the initial bounded parity result recorded in the [suite verification](amethyst-compatibility-suite-2026-11-08.md).

## Regression and package checks

- Full C# suite: 7,895 passed, zero failed, one existing Windows-only skip. It used the four native readers and pinned Caprica in an isolated Ubuntu container.
- Python suite: 31 passed, including package rejection when the fourth reader is missing and a SQLite fixture round trip preserving NUL-containing TEXT, BLOBs, numbers and quotes.
- Genuine rules-18/rules-19 producer fixtures cover complete provider order, physical source paths, root Top Level routing, conflict-only flags, disabled/excluded files, original producer identity, revision changes during a call, transaction races and wrong-reader refusal.
- Rules-19 named-source checks distinguish exact `Beta`/`beta`, refuse ambiguous `BETA`, retain unique case aliases for vanilla archives, and prevent selecting another provider when the exact requested owner lacks a file.
- Private self-contained package built with checksum-verified Microsoft SDK 10.0.404/runtime 10.0.15, untrimmed generated coverage and four pinned native modules. Both notice copies passed the publish-directory check. The archive probe validated payload hashes and native contracts, then initialized the packaged MCP server and listed all 34 tools.

Private archive SHA-256: `b26160712507dcc1d6492f2f7f37ed9629079b94e6de444204ac8819a45edf63`. Packaged server DLL SHA-256: `f0075c43220777c6e774b99fc2f5ffcf8aca1c60858a45922cdeb6d69a1ffd49`. The test archive retains the source's 2.1.3 version label and must not replace the published download. A release needs its own version and release acceptance.

Independent source review against the explicit base found no remaining actionable findings after corrections. The local code-only Graphify index and Code Review Graph were refreshed. Graphify reported 28,215 nodes and 78,155 edges, no missing/dangling endpoints or duplicate edges, and 163 self-loops. Its extractor skipped 25 unclassified files and 27 symbol-less code/data files. CRG updated without parse failures; its bounded impact output was truncated and untracked files were inspected directly. Static graph edges do not prove reflection-driven coverage.

## Limits

All runtime checks used disposable synthetic profiles, private HOME/XDG state, and read-only upstream/server inputs. No live Amethyst profile, game, prefix, mods, deployment or host registration was changed. The external comparison does not exercise writes; the C# suite supplies transaction/race coverage. These checks do not establish live deployment, Proton or gameplay acceptance. The [mapping policy](../architecture/amethyst-shared-reader-compatibility.md) still refuses unverified contracts and preserves captured producer identity.
