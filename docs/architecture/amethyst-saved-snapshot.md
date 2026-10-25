# Amethyst saved-snapshot preview and staging writes

## Contract

The 2.1.1 Linux preview accepts two exact producer contracts: API 13/schema 9,
engine 1/rules 13, and API 13/schema 10, engine 1/rules 14. Schema 9 uses the
existing `edde06e5` native reader. Schema 10 uses Amethyst 2.6.0-beta.7 at
`abeae84afce940c6819f80c1cd4e6e51db2e935d`, including preserved destination casing.
The source schema selects the matching reader before it opens the private copy;
there is no cross-schema fallback or migration of the live catalog. Unknown schema,
engine or rules combinations refuse. Revalidation requires the same captured
schema/rules pair even if a different supported pair appears during the call.

The earlier 2.1.0 package supports schema 9 only. The [2.1.1 verification record](../testing/upstream-211-compat-2026-10-25.md) covers schema-10 acceptance, archive absence handling and the final read-only profile check.

This Linux mode reads Amethyst's last complete saved filegraph. It consumes the
producer's native snapshot and exact source paths. It never refreshes,
migrates, deploys or writes the manager catalog. All Mutagen record coverage stays
generated; the record index has no plugin-count cap in this mode.

The published Linux 2.0.5 preview is built from houseCARL-Amethyst
`87e9268dc1052e31343fc8ff5bc014bd53b6c264`, integrating upstream houseCARL
`4e28505122165a1e210d0fd56d1382b377669421`. Its native filegraph component is
from stable Amethyst 2.5.4 at `edde06e518c0a3ca5ba47a8bbf065c4ecc4a8f92`.
The catalog contract is schema 9, engine 1, rules 13; the native API is 13.
Earlier reader checks used Amethyst Testing `e98cf70fe63f6b4a64b39e780ee3ffe02e553c6e`
and installed stable 2.5.4. Their Python and native filegraph source trees match
the stable pin and are unchanged from the previous Testing `97499673` pin.
The revision check describes the reader's tested range. A changed rules stamp
alone does not establish a breaking stored-data format change: tags 2.5.1 and
2.5.3 have the same schema, Python models and filegraph service as this Testing
pin; the native model changes only its rules constant. Routing/producer behavior
changed elsewhere, and complete parity with older releases remains untested.
The release targets the pinned stable contract. Future contracts require their own evidence.

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
Developer builds set `AMETHYST_FILEGRAPH_EXTENSION` to the schema-9 module and
`AMETHYST_FILEGRAPH_EXTENSION_SCHEMA10` to the schema-10 module. Packaged builds load
`amethyst_filegraph.abi3.so` and `amethyst_filegraph_schema10.abi3.so` beside the
server. The Python helper checks API and schema before opening the copy.
`AMETHYST_FILEGRAPH_PYTHON` optionally
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
result is published. These stamps detect inexpensive identity and freshness changes; they do not attest to content cryptographically.

Vanilla archive discovery reads the effective `Skyrim.ini` archive lists and
engine-loaded plugin bindings. A listed archive confirmed absent from the distinct
vanilla root does not make asset coverage incomplete. Shipped defaults can list
an absent `Skyrim - Patch.bsa`. Capture records these absent archive names and
revalidation refuses if a matching entry appears, including with different casing.
Directories and broken links at an archive path refuse; unreadable archive tables
remain explicit resolver failures. Missing effective INI or implicit-plugin sources
still mark vanilla archive coverage incomplete.

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

## Writes and deployment

Managed record and asset writes use durable staging transactions. New patches
preserve source files. Existing-plugin edits require their explicit lane, consent
and redeployment guards; see the [write contract](write-path.md). A failed source
or profile revalidation rolls back enrolled outputs instead of returning a
successful write against changed inputs.

Staging does not establish game visibility. Refresh, enablement, ordering and
deployment remain user-managed. Use `housecarl_staging_status` and the supported
`housecarl_staging_manage` recovery or deployment-verification action to inspect
recorded transactions. Missing or incompatible state refuses rather than silently
falling back to another manager or an inferred deployment.

Vanilla archive activation is implemented through the effective INI and
engine-loaded plugins, as described above. Genuine archive/configuration coverage
limits remain explicit. The [release verification record](../testing/upstream-211-compat-2026-10-25.md)
separates completed source/package checks from untested live deployment and gameplay.

## Evidence

The portable schema-9 fixture is generated by
`scripts/probes/build_amethyst_snapshot_fixture.py` using the pinned producer's
actual Python service and native extension with isolated synthetic files/config.
It retains SQL, intent payload, source bytes and producer winner/provider
expectations. The C# test world relocates it and writes real Mutagen records.

The schema-10 fixture uses the same generator with `--schema 10` and the beta.7
pin. It enables `preserve` casing and includes two mods with different physical
spellings of the same logical asset. The producer records the expected winner and
provider chain independently of houseCARL. Both fixtures exercise read parity,
source-change refusal and transaction-race rollback.

At these pins, 433 focused checks passed, including 34 saved-mode tests and the
affected upstream asset, artifact, configuration, scan/pole/refusal, parent-gather,
link-removal, dialogue-order and check-artifact tests. The
separately observed Windows sharing-lock case below is explicitly excluded from
that passing selection; this is not a claim that the full upstream suite is green.

Read-only acceptance against an installed 2.5.4 profile passed stdio startup and
32-tool discovery, explicit connection selection, 94-plugin resolution, three
real weapon summaries, a staged loose-mesh winner, and process restart. Profile
and manager configuration hashes, graph generations/readiness and winner counts
were unchanged. The native snapshot returned all 84 staged mod plugins where
the SQL projection held 80; the remaining ten plugins came from the distinct
vanilla root. No real manager inputs, mod files or deployed files were edited.

`AmethystSavedSnapshotTests` checks producer parity, full record reads, inactive
sources, archive keys, traversal, symlinks, stale intent/sources, producer races,
vanilla separation, export qualification and artifact rollback. Real stdio tests
in `AmethystSnapshotWireTests` check explicit opt-in, restart, text/JSON/pagination,
exported/structured provenance, stale-input MCP errors and write/preview refusal.

The upstream Windows sharing-lock artifact test expects rename to fail while a
file is open without share-delete. Linux permits that atomic rename; it is a
platform-specific baseline failure, not proof that the snapshot rollback failed.
The new rollback test checks restoration after an actual rejected snapshot read.

The staging regression fails with the previous blanket write guard and passes
with the new transaction. It verifies a real stdio dry run and patch write,
Mutagen readback, pending-deployment metadata and unchanged source/profile/graph
bytes. A separate generation-race test removes the new plugin and metadata while
preserving the source. Current tests load installed Amethyst 2.5.5-beta.1's API-13
native module; this does not claim real deployment or rollback acceptance.

On 2026-10-13, the focused writing/Amethyst selection passed 270 checks. The
123-check placement selection improved from 64 failures with the original
fixtures to 11, with no new failing cases. It still exposes Linux acceptance gaps (Windows ACL/creation-time
expectations, native source/destination paths, and MO2-only instance switching).
Those failures are not hidden or counted as a full Linux suite pass. The native
placement fixture repair is separate from the staging transaction change.
