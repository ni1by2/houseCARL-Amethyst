# Amethyst saved-snapshot preview

## Contract

This Linux mode reads Amethyst's last complete saved filegraph. It consumes the
producer's native snapshot and exact source paths. It never refreshes,
migrates, deploys or writes the manager catalog. All Mutagen record coverage stays
generated; the record index has no plugin-count cap in this mode.

The tested pins are houseCARL main `be6b67e1c7469bc91eabaad088d3347cb148e0bc`
and Amethyst Testing `97499673a39c8dadd603737363d5c74a4f272efb`
(2.5.4-beta.6), also exercised against installed stable 2.5.4. The catalog contract
is schema 9, engine 1, rules 13; the native API is 13. Amethyst's
Python and native filegraph source trees are unchanged from the previous
`de1f827` test pin; its Skyrim case-alias list changed outside those trees.
The revision check describes the reader's tested range. A changed rules stamp
alone does not establish a breaking stored-data format change: tags 2.5.1 and
2.5.3 have the same schema, Python models and filegraph service as this Testing
pin; the native model changes only its rules constant. Routing/producer behavior
changed elsewhere, and complete parity with older releases remains untested.
The target is current Testing. Future contracts require their own evidence.

## Selection

Call `housecarl_set_amethyst_connection` with an absolute schema-v1 manifest path
and `filegraph_mode="saved-snapshot"`. This validates the graph before changing
the connection, and saves `AmethystConnectionManifest` plus
`AmethystFilegraphMode` through the existing atomic user-config owner. Other saved
fields and unknown fork state survive. A persistence failure is reported: the
selection then lasts only for that process. Invalid input changes nothing.

For isolated startup, the equivalent configuration keys are
`HouseCarl:ConnectionManifest` and `HouseCarl:FilegraphMode`; their environment
spellings are `HouseCarl__ConnectionManifest` and `HouseCarl__FilegraphMode`.
Saved user settings take precedence. An unspecified mode stays unavailable; there
is no automatic fallback to saved data after a stricter check fails.

## Read boundary

`AmethystSavedSnapshot` captures the manifest, paths, deployment state, selected
profile inputs, effective shared or profile-specific staging roots, and distinct
vanilla root. Deployed merged `Data` is never a vanilla fallback; an active
deployment without `Data_Core` refuses.

The reader opens an existing SQLite database read-only with ordinary WAL support,
query-only enforcement and a two-second busy timeout. One transaction validates
versions, readiness, unfinished operations, foreign-key joins, selected variants,
inventory generation, profile generation and payload identities.
It checks the producer's BLAKE2b-192 profile-input hash against captured inputs.
MessagePack is decoded through a JSON writer, preserving binary values and embedded
NUL archive keys; its diagnostic JSON converter is not used.

The same transaction supplies a SQLite backup to a private temporary directory.
A short-lived Python 3.12+ helper loads the packaged Amethyst API-13 native module,
opens only that backup, and calls `open_profile(...).snapshot().iter_winners(...)`.
It uses the same restoration path as Amethyst itself and streams every winner page;
houseCARL does not implement the manager's winner algorithm. Native initialization,
locking and cache writes affect only the copy, which is removed after the helper
exits. No Python packages are required, and the .NET runtime remains bundled.
Developer builds set `AMETHYST_FILEGRAPH_EXTENSION` to the tested native module;
packaged builds load it beside the server. `AMETHYST_FILEGRAPH_PYTHON` optionally
selects the Python interpreter; the default is `python3`.

The database's `winners` table is a persisted projection, not the snapshot reader
Amethyst uses. Real-profile acceptance found four valid active plugins absent from
that table while Amethyst's native snapshot returned all 84 mod plugins at the
same profile/inventory generation. Their recorded staging sources exist. Therefore
that projection neither selects winners nor supplies a per-row freshness gate.
Native winner IDs, namespaces, destinations, mod identities and exact sources must
agree with the separately captured candidates and pinned generations.

Provider kind, target, destination and exact source are consumed as saved data.
Only Skyrim's game target is supported; unexpected exclusive-identity candidates
refuse instead of borrowing another game's identity-suppression rules. Winners are
never rebuilt by houseCARL from staging priority or merged game files. Root Data winners overlay
normal Data winners, and loose providers precede archives. Raw provider copies
are projected from selected saved variants for diagnostics. Inactive source lookup
also uses the saved inventory and requires a mod when a filename is ambiguous.

Native paths retain exact source casing. UTF-8, traversal, containment and symlink
checks precede source reads. Source size and nanosecond modification time must
match the catalog; observed device/inode/content stamps are rechecked before a
result is published. These cheap stamps are not cryptographic content attestation.

## Requests, warnings and artifacts

The MCP filter serializes dependent calls and connection changes. It captures one
graph, binds it through `AmethystSnapshotContext`, then revalidates inputs, canonical
roots, database identity/generation and observed sources before returning results.
A detected race returns an MCP error. New artifacts from that failed call are
deleted; replaced artifacts are restored. Cleanup failures are named.

Every dependent text response visibly carries:

> Using Amethyst's last saved filegraph. Changes made since its last refresh may be
> missing. Refresh the selected profile in Amethyst after changing mods or settings.
> These results do not confirm what is deployed in-game.

JSON responses, MCP structured content and JSONL manifest line 1 carry the same
`amethyst_snapshot` object: `profile`, `generation`, `inventory_generation`,
`mode="saved-snapshot"`, `freshness="unverified"`, and `warning`. Pagination,
`to_file` and automatic spill retain this qualification. Existing structured
content is preserved when metadata is added. No refresh time is inferred from a
SQLite file timestamp.

Changed profile intent, missing/changed sources, inconsistent joins, unsafe paths,
unfinished operations and unsupported contracts refuse. Out-of-band additions or
global settings not represented by the saved generation may remain invisible;
that uncertainty is what the warning covers. A new producer-owned live attestation
is optional future work, not a prerequisite for this mode.

## Availability and remaining work

Managed mutations and previews are refused before their bodies run, including
in-place consent side effects. Output-root access has a second service guard.
MO2 connection/update-cache operations remain unavailable on Linux. Explicit-path
archive and other manager-independent tools keep their existing behavior.

This preview resolves staged loose files, saved BSA members and distinct vanilla
loose files. **Vanilla BSA discovery is pending.** If the vanilla root contains
BSAs, asset results set `read_incomplete` and provenance adds
`asset_coverage_warning`; exports keep that warning too. An absent asset then
does not establish absence from those vanilla archives. Use explicit archive tools
to inspect them until the vanilla archive activation boundary is implemented.

The broader Step 5 consumer acceptance, real installed-profile acceptance,
staging writes and redeployment verification remain separate roadmap gates.

## Evidence

The portable schema-9 fixture is generated by
`scripts/probes/build_amethyst_snapshot_fixture.py` using the pinned producer's
actual Python service and native extension with isolated synthetic files/config.
It retains SQL, intent payload, source bytes and producer winner/provider
expectations. The C# test world relocates it and writes real Mutagen records.

At these pins, 348 focused checks passed, including 33 saved-mode tests and the
affected upstream asset, artifact, configuration and current scan/pole/refusal tests. The
separately observed Windows sharing-lock case below is explicitly excluded from
that passing selection; this is not a claim that the full upstream suite is green.

`AmethystSavedSnapshotTests` checks producer parity, full record reads, inactive
sources, archive keys, traversal, symlinks, stale intent/sources, producer races,
vanilla separation, export qualification and artifact rollback. Real stdio tests
in `AmethystSnapshotWireTests` check explicit opt-in, restart, text/JSON/pagination,
exported/structured provenance, stale-input MCP errors and write/preview refusal.

The upstream Windows sharing-lock artifact test expects rename to fail while a
file is open without share-delete. Linux permits that atomic rename; it is a
platform-specific baseline failure, not proof that the snapshot rollback failed.
The new rollback test checks restoration after an actual rejected snapshot read.
