# houseCARL-Amethyst

houseCARL-Amethyst is an MCP server for Skyrim Special Edition load orders managed by [Amethyst Mod Manager](https://github.com/ChrisDKN/Amethyst-Mod-Manager). It reads Amethyst's saved profile snapshot and gives an AI assistant access to plugin records, game assets, compiled Papyrus, SKSE configuration, and SkyPatcher INI data. It uses [Mutagen](https://github.com/Mutagen-Modding/Mutagen) and runs in Claude Code or OpenAI Codex.

**IMPORTANT NOTICE:** houseCARL-Amethyst is a vibe-coded project. I am not a dev - I pointed Codex at the original houseCARL and Amethyst projects and asked to make a port. Avick3110, the maintainer of houseCARL, gave me permission to port this to Linux as he doesn't work with Linux. There is an eventual intention to re-factor all of this into Rust, but I want to be clear right now about what this project currently is. While this does work as intended, I am not pretending to have made anything original here; I wanted the functionality created by Avick3110 applied to my own situation, and burned tokens for it. Publishing so that anyone else who wants the same doesn't have to duplicate any token spend or effort. I'm open to all pull-requests and contributions of code.

**IMPORTANT NOTICE 2:** both upstream projects are being heavily developed. This tool may not always have caught up. If you face any issues using houseCARL-Amethyst, _**DO NOT BOTHER THE ORIGNAL DEVS**_

## Requirements

### Linux / Amethyst preview

- Linux x64, Python 3.12 or newer, and Amethyst. Version 2.5.4 is the tested stable release. The reader expects filegraph API 13 and database schema 9; other Amethyst versions require compatibility verification even if those numbers match.
- The operating-system runtime libraries listed in the [Linux installation guide](docs/linux-install.md). The package includes the .NET runtime, Amethyst's native filegraph component and native Papyrus utilities.
- Claude Code or OpenAI Codex to host the MCP server.

Source builds additionally require the .NET 9 SDK and Rust/C/C++ build tools. See the [Linux development and build guide](docs/linux-development.md) for the pinned dependencies and build steps. Linux support is in preview.

## Install

Download the archive, checksum and standalone installer from [v2.0.5 — Linux preview](https://github.com/ni1by2/houseCARL-Amethyst/releases/tag/v2.0.5). This is a prerelease. Follow the [Linux installation guide](docs/linux-install.md) to verify the download, install it, and select your Amethyst saved snapshot. Running the package does not require the .NET SDK or build tools.

## Usage

```
> connect houseCARL to my Amethyst Skyrim profile

> which plugins override Hulda's NPC record, and what does each change
  records: formids=[<Hulda>], project={"form": "tree"}

> which line answers Hulda's greeting, and which plugin moved it
  records: types=["DIAL"], where=["Quest = <quest>"], project={"form": "info_order"}

> set the iron sword's damage to 12, into a patch called IronRebalance
  records: formids=["013989:Skyrim.esm"], project={"form": "fields", "fields": ["BasicStats.Damage"]}
  apply:   ops=[{"formid": "013989:Skyrim.esm", "field_path": "BasicStats.Damage", "op": "Set", "value": "12"}],
           patch="IronRebalance", dry_run=true
  apply:   the same call without dry_run

> every weapon in the order whose winning damage is 50 or more, as a table
  records: types=["WEAP"], where=["BasicStats.Damage >= 50"],
           project={"form": "fields", "fields": ["BasicStats.Damage"]}, format="dense"
```

A write resolves the record's load-order winner and overrides it into the patch. It edits that record once. Use `project={"form": "tree"}` to see how earlier plugins changed it; the write does not repeat the edit across those plugins.

New patches and managed assets go through Amethyst's transaction workflow and remain in staging until deployed. Refresh and deploy them in Amethyst, then use houseCARL's deployment check to confirm which file wins in the game.

## How it works

houseCARL runs as one MCP process over stdio. Amethyst supplies a saved profile snapshot and filegraph. Each response that depends on them identifies the profile and generation it used, since changes made after Amethyst's last refresh may be missing. Mutagen parses records from its lazy binary overlay as needed. houseCARL does not hold plugin file handles between calls.

houseCARL checks freshness by comparing each file's last-write time and size. It replaces the current index with a new immutable snapshot when it rebuilds. If it cannot read a plugin, it excludes that plugin and gives the reason in each response from that snapshot.

The write oracle has 17 cells. Each applies a mutation through the reflection engine and a typed Mutagen setter, then requires the output plugins to match byte for byte. Since the engine does not depend on record type, each cell covers every record that supports that operation. The oracle is also run by hand against a real `Skyrim.esm`. CI runs 134 probe harnesses against real plugins.

Nexus lookups are the only tools that use the network. They need no account or API key. When offline, the lookups report that status and local tools keep working. `update_status` reads cached Amethyst metadata; explicit update checks use the installed file ID.

`decompile_script` reconstructs `.psc` files from `.pex` without an external tool. In a 3,400-plugin order, 10,189 script pairs could be compared; 98.80% decompiled and recompiled byte for byte. The output identifies scripts it cannot prove.

## Amethyst connection and saved snapshot

Select Amethyst's schema-v1 saved-snapshot connection with `housecarl_set_amethyst_connection`,
using `manifest_path` and `filegraph_mode="saved-snapshot"`. The selection persists across
restarts. Each dependent response and JSONL export identifies the saved profile and generation,
and warns that changes made since Amethyst's last refresh may be missing.

The adapter reads record winners and asset inputs from one captured profile state, including
vanilla archives and effective profile inputs. Record and asset writes use durable transactions
to place output in staging. They leave categories, priorities, plugin enablement, and the live
deployment unchanged. Output stays pending until Amethyst refreshes and deploys it. A later
verification records whether the expected file won. `update_status` reads cached Nexus metadata;
explicit update checks use the installed file ID.

The [Linux installation and current limits](docs/linux-install.md) page describes the published
installer. The [2.0.5 verification record](docs/testing/upstream-205-integration-2026-10-15.md)
distinguishes the final release checks from earlier candidate results.

The release integrates upstream houseCARL 2.0.5 at `4e285051` and bundles the native
component from stable Amethyst 2.5.4 at `edde06e5` (native API 13; schema 9, engine 1, rules 13). houseCARL
has read a real installed 2.5.4 profile. The Linux package bundles the .NET runtime and
Amethyst's native component. **Python 3.12+ must be installed.** The native reader opens
a private catalog copy and does not write manager files. The [snapshot contract](docs/architecture/amethyst-saved-snapshot.md)
covers configuration, checks, and limits. Amethyst must close normally before a managed write
that needs its library lock. The server refuses that write while Amethyst is open. Papyrus and
BSA tools use the selected native Steam/Proton environment where supported. An isolated
BSArch pack/list/extract round trip passed with Proton CachyOS and a copied prefix.
Flatpak and custom Steam runner layouts remain unsupported. Live-profile deployment,
the manager GUI workflow and gameplay were not tested.

| | |
|---|---|
| Process | C#/.NET 9 MCP over stdio; Linux snapshot reads use a short-lived Python helper |
| Substrate | Mutagen.Bethesda.Skyrim 0.54.4 |
| Tools | 34 registered; Linux availability described above |
| Skills | 7 |
| Record coverage | 133 record types, 242 sub-structures, 497 polymorphic arms, 280 enums, generated at build time |
| Licence | GPL-3.0-only |

## Design

The design follows four rules.

1. **Record coverage is generated.** A build-time generator reflects over Mutagen's record interfaces and emits the schema and validation data. houseCARL handles every record type Mutagen models. When Mutagen lacks a type supported by xEdit, the tools report that gap.
2. **One grammar, closed under composition.** Every operation is one call composed from orthogonal axes: select a set of records, project what to read, apply one of a fixed set of write verbs.
3. **Errors name the problem and the next step.** Tools explain wrong or incomplete results. Before opening a file for writing, preflight names unknown fields, invalid verbs or enum values, and FormLinks used with the wrong record type.
4. **Reads are lazy; freshness is checked cheaply.** Records parse from a binary overlay as needed. houseCARL does not keep the load order in memory and detects disk changes with a last-write-plus-size check on the next call.

## The tool surface

All tools carry the `housecarl_` prefix.

| Substrate | Tools |
|---|---|
| Records | `records` `check` `apply` `create` `remove` `forward` `copy` `write_seq` `create_plugin` `compact_plugin` `merge_plugins` |
| Assets | `asset_status` `place` |
| NIF | `nif_inspect` `nif_set` |
| Papyrus | `compile_script` `decompile_script` |
| BSA | `bsa_list` `bsa_extract` `bsa_repack` |
| SkyPatcher | `skypatcher_layer` |
| SKSE | `skse` |
| Nexus | `nexus_search` `nexus_mod` `nexus_graphql` `nexus_check_updates` `nexus_identify` |
| Session | `set_amethyst_connection` `set_tool_path` `load_order_status` `update_status` |
| Transactions | `staging_status` `staging_manage` |

`records` is the read surface for the record plane. A read is composed from four axes:

| Axis | Decides | Values |
|---|---|---|
| SELECT | which records | `formids` `types` `plugins` (with `defined_in`) `conflicts_only` `where` `references` `walk` |
| SOURCE | whose version | the winner (default), a plugin filename active or not, a `{file, mod}` pair, the SkyPatcher overlay pre or post replay; `versus` names a second pole for `delta` and `tree` |
| PROJECT | the shape of the answer | `identity` `summary` `fields` `rows` `everything` `aggregate` `delta` `tree` `chain` `info_order` |
| TRANSPORT | the rendering | `format` (text, json, dense) `limit` `offset` `max_chars` `counts_only` `to_file` |

A write is composed from the op list, the lane and the transport:

| Axis | Values |
|---|---|
| Verb | `Set` `Add` `Remove` `SetAtIndex` `InsertAtIndex` `ReplaceAll` `Merge` `CopyFrom` |
| Lane | `patch=` a new plugin (default), `into=` an existing houseCARL patch, `in_place=` a named plugin overwritten, `dry_run=` |
| Transport | `readback` `format` `max_chars` |

For example, this read finds each WEAP touched by Requiem.esp whose damage, as set by Requiem.esp, is at least 50. It reads two fields from that plugin. Set `where_source="winner"` and `fields_source="winner"` to ask the same question about the load-order winner.

```jsonc
housecarl_records(
  plugins = {"names": ["Requiem.esp"]},
  types   = ["WEAP"],
  where   = ["BasicStats.Damage >= 50"],
  project = {"form": "fields", "fields": ["BasicStats.Damage", "Keywords"]})
```

This write applies two ops to one record. Both are validated before any file is changed.

```jsonc
housecarl_apply(
  ops = [
    {"formid": "013989:Skyrim.esm", "field_path": "BasicStats.Damage", "op": "Set", "value": "12"},
    {"formid": "013989:Skyrim.esm", "field_path": "Keywords", "op": "Add", "value": "0A8668:Skyrim.esm"}],
  patch   = "IronSwordRebalance",
  dry_run = true)
```

`dry_run` runs the full pipeline: winner resolution, schema preflight, in-memory application of each op, and reference checks. It stops before writing and returns either the proposed changes or the refusal the real call would produce.

**Predicates.** `where=` accepts comparisons (`BasicStats.Damage >= 50`), EditorID tests (`editorid startswith REQ_`), negated string operators (`Name not contains Dagger`, `not startswith`), bit-field tests (`BodyTemplate.FirstPersonFlags has Body`, `has_any`, `has_none`), presence (`VirtualMachineAdapter exists`), file-based membership (`formid not in @<path>`), a link step (`Perks->editorid startswith REQ_NULL_`), provenance (`winner = X.esp`), containment (`*parent.EditorID`), and quantified list steps (`Effects[*none].BaseEffect->editorid startswith REQ_`, `Effects[*count] > 2`). Predicates are ANDed.

**FormIDs.** Address a record as `XXXXXX:Plugin.esp` or use the runtime form printed by the console, Papyrus log, or crash log (`FExxxYYY`, `XX######`). houseCARL resolves runtime IDs against the current order. Reads accept both forms. Writes require the plugin form and return it when given a runtime ID.

**Large results.** Every record response carries an `epoch` stamp for the index build that produced it. When a result exceeds the render limit, houseCARL writes the full result to JSONL, with a manifest on the first line, and returns the file path. Pass that file to a later call as `formids=["@<absolute path>"]`; houseCARL checks the epoch. A plain file with one plugin filename per line can scope `plugins={"names": ["@<absolute path>"]}` or `check`'s `plugins=` and `exclude=`. Prefix an inline plugin filename that starts with `@` with `@@`.

**Findings.** `check` runs four finding families over the load order: `errors` (dangling FormLinks, missing masters, unparseable records), `scripts` (script properties the VMAD does not bind), `dialogue` (dialogue graph checks for seeded topics and quests), and `facegen` (the winning head `.nif`, face `.dds`, and NPC record for each NPC, plus the mismatch class). Dialogue checks use the supplied seeds. Each family description lists its limits.

## Coverage

At build time, reflection generates the schema from Mutagen.Bethesda.Skyrim 0.54.4. It covers 1,174 types at full field depth and records each field's type, cardinality, writability, nullability, and xEdit signature. The `mutagen-reference` skill carries the same data for offline lookup.

If Mutagen does not model a record type, the reference and tools do not include it. A request for that type returns a named library coverage gap. The server is published with trimming off because trimming a reflection-driven server can remove types and silently reduce coverage.

Record identity is not writable. `FormKey` on a record, and `ModKey` and `Master` on the mod header and master references, are refused by the write pre-flight and reported as not writable by the reference.

## Runtime layers

Four layers below the plugin plane affect what the game runs. houseCARL reads each of them.

- **Amethyst's saved asset view.** Which loose or archived file wins for a Data-relative path, with its saved source and conflict chain. Managed asset writes go to Amethyst staging and remain pending until refresh and deployment.
- **NIF internals.** Shape names, embedded skin and FaceTint texture paths, flags, alpha, partitions, read from the winning copy. A whitelisted subset is written back with readback verification.
- **The SKSE plugin layer.** houseCARL finds each DLL and configuration file under `Data\SKSE\Plugins` and resolves its winning source. It reads declared DLL version metadata without loading the DLL. It also checks that DLLs implement native Papyrus functions declared by scripts and that SKSE configuration files reference plugins in the load order.
- **The SkyPatcher INI layer.** houseCARL reads INIs in apply order, including VFS shadows and conflicts between INIs. It can also replay the layer over a record or include a draft INI that is not yet in a mod folder.

The SKSE and SkyPatcher tools report what a file declares. They do not observe the running game.

Two precedence orders determine an NPC's face: the file view selects the baked files, and the load order selects the record. `check findings=["facegen"]` compares them. See [docs/facegen.md](docs/facegen.md) for causes and fixes.

## Writing

Three lanes, mutually exclusive.

| Lane | Parameter | Target | Existing files |
|---|---|---|---|
| New patch (default) | `patch=` | A new mod folder `houseCARL - <name>` holding one plugin whose masters span every referenced plugin | Untouched |
| Extend | `into=` | An existing houseCARL patch; a record already in it is edited, one not in it is copied in from the winner first | The named patch only |
| In place | `in_place=` | The named plugin, including one houseCARL did not author; re-laid out on save as xEdit and the CK do | The named plugin is overwritten |

A patch name already taken by an earlier houseCARL patch is suffixed. A name matching a plugin on disk that the order is not loading is refused, naming the place and the file.

The in-place lane does not create a backup or provide an undo. On the first write to a plugin, houseCARL asks for confirmation; repeat the call with `acknowledge=true` to proceed. That acknowledgement covers only the named plugin. Every call runs preflight and verifies the record. Before writing, houseCARL serializes the original plugin in memory and refuses the write if any record would lose a subrecord or change length. The refusal names the affected record and subrecords, and leaves the file untouched. `into=` applies the same check to the patch being extended. Four measured losses that carry no information are allowed: an all-zero LTEX `INAM` below form version 43, a deleted REFR's `NAME`, an all-zero REFR `XRMR`, and a RACE `PHWT` written back as its own leading bytes with an all-zero tail cut.

**Preflight.** Before opening a file for writing, houseCARL checks every op's record type, field path, enum value, verb and cardinality, value range, writability, record identity, and FormLink target type. It names and counts each failing op. One invalid op refuses the entire call.

**Readback.** houseCARL reads each edited field from the output file. Set `readback=true` to read every touched record in full. The response describes the file's contents. A staged patch affects the game only after Amethyst refreshes and deploys it. houseCARL can then check whether the expected file won.

The write tools also create records with fresh FormIDs, including nested dialogue structures, and remove records or list entries. They can forward a named plugin's record as the winning override, restore a vanilla record, copy an NPC's appearance into a standalone record, create an empty plugin, ESL-compact a plugin while carrying its FormID-keyed FaceGen and voice files, and merge plugins while renumbering only collisions. Every write trims unused masters.

Dialogue records have bookkeeping that a byte-valid insert does not satisfy: [docs/dialogue.md](docs/dialogue.md).

## Skills

The plugin ships seven skills, invoked as `/housecarl:<name>` in Claude Code or `$housecarl` in Codex. Each skill has a `SKILL.md` and a `references/` tree. Skills search reference corpora as needed instead of loading them whole. Four skills, `mutagen-reference`, `papyrus-reference`, `spid-authoring`, and `kid-authoring`, include a `references/index.jsonl` that maps names to files. The two generated corpora include the indexed line as well.

| Skill | Carries |
|---|---|
| `mutagen-reference` | The schema of every modelled record type: fields, types, cardinality, writability, enum values. Emitted by the same generator pass as the server's rulebook. |
| `papyrus-reference` | Papyrus, SKSE and shipped SKSE-plugin API signatures (PapyrusUtil, JContainers, MCMHelper, po3, SkyUI), 7,345 entries, from [papyrus-index](https://github.com/BellCubeDev/papyrus-index). A function the corpus does not carry is reported as absent. |
| `skypatcher-authoring` | SkyPatcher 6.4.1 INI grammar and the offline check of a draft INI through the overlay. |
| `spid-authoring` | SPID 7.3.0 `_DISTR.ini` grammar. |
| `kid-authoring` | KID 3.5.0 `_KID.ini` grammar. |
| `open-animation-replacer` | OAR 3.0.0 `config.json` / `user.json` conditions, submod priorities, DAR `_conditions.txt` conversion. |
| `skse-plugin-authoring` | Native SKSE plugin DLLs on CommonLibSSE-NG: lifecycle, event sinks, trampoline and Address Library hooks, native Papyrus functions, SE + AE + VR from one DLL. |

Dialogue and facegen tools handle their own bookkeeping. Their measured behavior is documented in [docs/dialogue.md](docs/dialogue.md) and [docs/facegen.md](docs/facegen.md), which the tool descriptions link to.



## Development

| Path | What |
|---|---|
| `src/housecarl-mcp/` | The MCP server and tool surface |
| `src/housecarl-core/` | Record, asset, read and write engines; the load-order resolver |
| `src/housecarl-generator/` | Build-time schema generator; the probe runner |
| `src/housecarl-mcp-tests/` | xUnit tests against the built server |
| `scripts/` | Linux package, native-tool and installer workflows |
| `plugin/` | Plugin manifest, changelog, notices; skills are copied in at build |
| `.claude/skills/` | Skill sources |
| `docs/` | Architecture notes and decision records |
| `standards/` | Testing and naming |

Use the Linux build and test commands in [docs/linux-development.md](docs/linux-development.md).

Before changing the code, read [CLAUDE.md](CLAUDE.md) and the architecture note for the subsystem in `docs/architecture/`. Each decision has its own file in `docs/decisions/`. See [CONTRIBUTING.md](CONTRIBUTING.md) for the contribution process.

## Licence

houseCARL-Amethyst inherits GPL-3.0-only from the [original houseCARL project](https://github.com/Avick3110/houseCARL). Upstream houseCARL uses and bundles Mutagen and other GPL-3.0-only libraries without a linking exception, so the combined program must be distributed under GPL version 3. The bundled Amethyst filegraph is GPL-3.0-or-later and is distributed with this program under version 3. Caprica retains its MIT licence. See [LICENSE](LICENSE) and [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) for the licence texts, component notices, and source links.

If you distribute houseCARL-Amethyst or a modified version, keep the copyright and licence notices, identify your changes, and license the covered combined work under GPL-3.0-only. Distributing binaries also requires providing their Corresponding Source in a form permitted by GPLv3, including modified source and the scripts needed to build and install it. Separate bundled components retain their own licence notices. These obligations are explained in [GPLv3, sections 4 to 6](https://www.gnu.org/licenses/gpl.en.html).

You can run the program and make private changes without publishing your source. Using it does not automatically place your mods or generated patches under the GPL; output is covered only when its contents themselves form a covered work. Any source material used in a patch still carries its own permissions and licence requirements. See the [GNU licence FAQ](https://www.gnu.org/licenses/gpl-faq.en.html#GPLOutput).

## Credits

- [Amethyst Mod Manager](https://github.com/ChrisDKN/Amethyst-Mod-Manager) by ChrisDKN and contributors, whose manager and native filegraph provide the profile and asset view used by this fork.
- [Original houseCARL](https://github.com/Avick3110/houseCARL) by Avick3110 and contributors, the project this Amethyst integration builds on.
- [Caprica](https://github.com/Styyx1/Caprica), originally by Orvid King, with the fork used here maintained by Styyx1. It provides the native Papyrus compiler.
- [Mutagen](https://github.com/Mutagen-Modding/Mutagen) by Noggog, the Bethesda-format library used by houseCARL.
- [papyrus-index](https://github.com/BellCubeDev/papyrus-index) by BellCube, the source corpus for `papyrus-reference`.
- Zzyxzz for SkyPatcher and powerofthree for SPID and KID. Their public documentation informed the distributor grammars.
- DrHeisen for the `open-animation-replacer` skill and two earlier skills that have since been retired.
