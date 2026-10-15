# Amethyst saved-snapshot preview and staging writes

## Contract

This Linux mode reads Amethyst's last complete saved filegraph. It consumes the
producer's native snapshot and exact source paths. It never refreshes,
migrates, deploys or writes the manager catalog. All Mutagen record coverage stays
generated; the record index has no plugin-count cap in this mode.

The tested pins are houseCARL main `b77ba4b202456affe30dd51111972529e469c278`
and Amethyst Testing `e98cf70fe63f6b4a64b39e780ee3ffe02e553c6e`
(2.5.4), also exercised against installed stable 2.5.4. The catalog contract
is schema 9, engine 1, rules 13; the native API is 13. Amethyst's
Python and native filegraph source trees match stable main `edde06e5` and are
unchanged from the previous Testing `97499673` pin.
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
result is published. These stamps detect inexpensive identity and freshness changes; they do not attest to content cryptographically.

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

New record patches through `apply`, `create`, `forward` and `copy` use the same
reflection-driven write engine as upstream. The request filter captures and
serializes their saved inputs. Only a guarded new-patch call can allocate staging
output; direct read callers still fail the service's output-root guard. `into=`
and `in_place=` remain refused before tool bodies or consent side effects run.

Allocation registers the new plugin and metadata for rollback before writing
either file. If profile/configuration, catalog generation or an observed source
changes before the response, both artifacts are removed and the empty new folder
is removed. An unrelated file added to that folder is preserved. Failed writes
also roll back registered artifacts. A successful patch retains its profile,
source generation, `pendingDeployment=true` and SHA-256 in `meta.ini`; inline JSON
and structured MCP content report `amethyst_staging.state=pending_deployment`.
Text replies state the same limitation. Dry runs create no folder or metadata.

No call changes Amethyst's profile ordering, plugin activation, graph or deployed
game files. Refresh, placement, enablement and deployment are user-managed. These
pending markers do not clear automatically yet: deployment verification is still
a release gate. This is an implementation checkpoint toward full original
houseCARL writing support, not a reduction of that release target.

Other managed mutations are refused before their bodies run.
MO2 connection/update-cache operations remain unavailable on Linux. Explicit-path
archive and other manager-independent tools keep their existing behavior.

This preview resolves staged loose files, saved BSA members and distinct vanilla
loose files. **Vanilla BSA discovery is pending.** If the vanilla root contains
BSAs, asset results set `read_incomplete` and provenance adds
`asset_coverage_warning`; exports keep that warning too. An absent asset then
does not establish absence from those vanilla archives. Use explicit archive tools
to inspect them until the vanilla archive activation boundary is implemented.

The remaining release gates are vanilla archive activation/coverage, extending
owned patches, guarded existing-plugin edits with consent and redeployment
confirmation, other managed write consumers, deployment verification, a complete
Linux test pass, clean installation/update/rollback, and independent review.

## Evidence

The portable schema-9 fixture is generated by
`scripts/probes/build_amethyst_snapshot_fixture.py` using the pinned producer's
actual Python service and native extension with isolated synthetic files/config.
It retains SQL, intent payload, source bytes and producer winner/provider
expectations. The C# test world relocates it and writes real Mutagen records.

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
