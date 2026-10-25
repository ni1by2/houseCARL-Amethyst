# Linux 2.1.1 verification — 25 October 2026

The 2.1.1 Linux preview restores saved-snapshot access for Amethyst 2.6.0-beta.7
and corrects the vanilla BSA coverage warning for confirmed absent archive names.
The headless API refactor remains deferred.

## Source and native contracts

The implementation was tested at `7cd86a03d6848393080cc365a15f8190d6ceba2d` and
merged in [PR #16](https://github.com/ni1by2/houseCARL-Amethyst/pull/16) as
`9f707aaeb89535ef686202304023127560c33d32`. The merged Git tree matches the tested
head. Upstream houseCARL remains at the 2.1.0 engine integration, `1391dacd`.

| Reader | Amethyst source | Contract |
|---|---|---|
| Schema 9 | stable 2.5.4, `edde06e518c0a3ca5ba47a8bbf065c4ecc4a8f92` | API 13, engine 1, rules 13 |
| Schema 10 | 2.6.0-beta.7, `abeae84afce940c6819f80c1cd4e6e51db2e935d` | API 13, engine 1, rules 14 |

Capture selects the reader before opening the private catalog copy. Unknown or
mismatched contracts refuse; no live catalog migration or cross-schema fallback
is performed. Durable staging journals retain the captured producer contract.

## Completed checks

[CI passed on the exact implementation head](https://github.com/ni1by2/houseCARL-Amethyst/actions/runs/40173386470),
using Ubuntu 24.04, .NET 9, Python 3.12 and both pinned native readers:

- Full C# suite: 7,373 passed, one Windows-only skip, zero failures.
- Python installer/package suite: 21 passed.
- Fourteen focused archive cases passed across both schemas, covering absent
  configured archives, appearance with different casing, removal, directories,
  broken links and malformed archive tables.
- Producer-generated fixtures cover winner/provider parity, preserved physical
  casing, exact producer-contract changes and staged-write rollback.
- The self-contained archive passed payload hashes, both native module probes,
  MCP version 2.1.1 and all 34 tools.

Read-only acceptance against a refreshed beta.7 saved profile passed at generation
54. Status resolved the selected plugin files without the blanket vanilla BSA
coverage warning. `scripts/actor.pex` included `Skyrim - Misc.bsa` in its provider
chain and reported `read_incomplete=false`. Protected profile and connection input
hashes were unchanged.

## Release package

Release preparation changes documentation only. The runtime, native readers,
Papyrus utilities, skills and standalone installer are byte-identical to the
verified implementation archive. No runtime or native component was rebuilt for
this documentation pass, and the completed test suites were not rerun locally.

The release payload manifest differs from the tested archive only in `README.md`,
`docs/linux-install.md` and `docs/amethyst-saved-snapshot.md`. The repacked archive
passed its own payload-hash, native API/schema and MCP startup/tool-list probe.
Packaged relative links resolve; references to files outside the archive use
source URLs pinned to the 2.1.1 tag.

Release archive SHA-256:

```text
5b814b8c951140c3766f7d30ad392a510ee6ef8d8bb02209bbf79854e21d82e8
```

The checksum asset and release notes identify the downloadable archive. The
source tag also contains the updated development guide, changelog and this record.

## Limits

No installed server or host registration was changed. No live profile write,
mod ordering, activation, deployment, Steam mapping or prefix change was performed
for 2.1.1 validation. Saved-snapshot freshness remains unverified; the profile check does
not establish what the game loads.

The installer and pinned Papyrus tools are unchanged. System installation,
upgrade/rollback and the disposable Proton CachyOS utility round trip retain the
[2.0.5 evidence](upstream-205-integration-2026-10-15.md); those workflows were not
repeated for this release. Live-profile deployment and gameplay remain untested.
