# Upstream 2.1.2 integration — 31 October 2026

This is an unreleased implementation on `codex/amethyst-upstream-212-assets`. It does not replace the published 2.1.1 Linux preview. No live profile, game files, deployment, host registration or installed server was changed.

## Sources and scope

- Published fork base: `cbdf54dae82060e7288daa58d8417f8fc41fa5f2`.
- Owner-metadata fix: `a4fd2efa720015e05435710732c2a8effac2ad53`.
- Upstream houseCARL: `1bdd0a8d1f34a7cef99a856ac61d0f4fb33cb732`, v2.1.2.
- Amethyst reviewed at `c25cffff2585a5615433c5dc96d0be47d9a68bb9`, v2.6.0. The existing API-13 schema-9/rules-13 and schema-10/rules-14 reader pins remain unchanged.
- Framework migration: `6df0bd07`. Tested engine integration: `1be08332`.

The integration includes partial-form winners, faction ownership ranks, excluded-plugin touch diagnostics, compaction header and moved-ID fidelity, record projection/agreement fixes, header-flag names, UTF-8 provenance, generated empty-subrecord reports, dialogue branch/quest diagnostics and asset dependency traversal.

Source builds use the .NET 10 SDK selected by `global.json`. Validation used SDK 10.0.403 and bundled runtime 10.0.14. The optional HTTP transport, Linux startup, XDG state and Amethyst transaction guards are retained. Upstream's Windows-only startup refusal and test startup hook were not imported.

## Amethyst adaptations

Provider selection and named disabled donors use selected saved variants and native winners. They do not rediscover managed files from staging directories or apply MO2 skip rules over Amethyst. Unknown providers and changed source identities refuse. Root-folder providers participate in the provider selector.

The language binding reads captured effective profile/prefix INIs, including case variants and UTF-16 BOMs. Localized reads use private copies of selected string-table bytes, allowing different table kinds to come from different saved providers without finding excluded files beside them. The request removes those copies after use. Linux archive string reads in the retained legacy lane use the same private-copy approach.

Asset Walk normalizes Bethesda paths separately from host paths, reports traversal bounds and missing dependencies, and checks NPC Traits template chains before voice resolution. A loop produces a voice-resolution refusal rather than entering Mutagen's recursive lookup. Separator-only selectors refuse; `**` selects the Data root.

## Executed checks

- Full Linux suite: **7,841 passed, zero failed, one existing Windows-only skip**; 7,842 total.
- Python installer/package suite: **21 passed**.
- Native fixture cases cover both schemas: record → mesh → texture traversal, disabled donors, excluded files, native visibility instead of MO2 suffix rules, provider filtering, root-folder providers, source changes, BSA members and MCP snapshot warnings. Additional cases cover INI encoding, exact string-table copies and a voice template loop.
- A clean, untrimmed self-contained archive built successfully. A seeded stale server file was removed before publication.
- Archive verification loaded API-13 native readers for schemas 9 and 10, initialized MCP and listed all 34 tools. The packaged runtime configuration includes .NET and ASP.NET Core 10.0.14. The packaged server also started in the previous validation environment without a system .NET 10 runtime.
- Dependency notices were generated from the Linux publish and checked against both tracked copies: 45 components, including runtime packs. Amethyst and Papyrus-tool notices remain included.
- Generated coverage remains 133 record types, 242 structs, 497 polymorphic arms and 280 enums, from Mutagen 0.54.4. Existing read-only projection anomalies remain visible.

Validation used disposable containers, synthetic profiles and private state. The native utilities were reused from the earlier verified package because their sources and pins did not change. No Proton, live deployment or gameplay acceptance was repeated.

## Review and remaining release work

Graphify and Code Review Graph were refreshed locally; the diff base was the published fork commit above. Graphify reported duplicate namespace IDs and a retained source outside its new scan. Important connections were checked against source and executed regressions. Graph edges and test-gap hints do not establish reflection coverage or independent review.

The implementation has not received the separate review required by `CLAUDE.md`. Remote CI, merge, release versioning/publication, installation and read-only acceptance of the user's current live profile remain separate work. The Amethyst headless API refactor remains deferred.

The private validation archive still carries the source manifest's 2.1.1 label. It is an internal candidate, not a replacement download for that published release. Assign the next release version before distribution.
