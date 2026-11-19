# Upstream 2.2 integration and Linux compatibility

This development branch integrates [houseCARL 2.2.0](https://github.com/Avick3110/houseCARL/releases/tag/v2.2.0), commit `21ae5f19ae6ed796a7931c1ce786011551c9ee52`, over our previous upstream pin `a4dc3ebee6ce2a0b2f8672eb5bb77fcd75206a0f`. Publication requires a separate release decision. The published Linux package remains 2.1.5.

## Linux remains a fork responsibility

Upstream targets Windows and MO2. This fork retains native Linux path conversion and case handling, Caprica and Champollion, selected Skyrim Proton execution for Windows utilities, XDG configuration, the installer-owned Codex skill, and self-contained .NET 10 packaging. There is no new runtime migration: 2.1.5 already targets .NET 10.

Amethyst still supplies the saved profile, source identities, routing, exclusions and provider order. No MO2 skip rule or directory rescan replaces that view. Writes retain transaction recovery, per-plugin consent and per-call redeployment consent. Default output goes to a new staging mod. Updating this server does not refresh, reorder, enable or deploy the user's mods.

The imported per-call filesystem and archive helpers have Linux adaptations. Host paths retain case-sensitive identities; virtual Skyrim addresses use Bethesda separators and case rules. Ambiguous case-equivalent sources refuse. Archive-table caching retains the corrupt-folder-index fallback. New tool refusals carry MCP `isError`, including refusals inside the Amethyst transaction filter.

## Tool-call migration

| Earlier API | 2.2 API |
|---|---|
| `housecarl_skypatcher_layer` | `housecarl_layer`; supports SkyPatcher, SPID, KID, BOS and FLM |
| `asset_status asset_paths=` | `asset_status paths=` |
| `nif_inspect mesh_paths=` | `nif_inspect paths=` |
| `nif_inspect npc=` | `nif_inspect formids=` |
| `nif_set mesh_path=` | `nif_set paths=`; follow its schema's selection rules |
| `decompile_script pex=` | `decompile_script paths=`; supports Data-relative scripts, disk files, `under=` and `provider=` |
| `copy exclude_types=` | `copy exclusions=` using the shared walk grammar |

Retired names give a refusal naming the replacement. Refresh the MCP host's tool inventory after changing server versions. The installed release's published tool schema is authoritative.

For example, inspect and decompile saved scripts:

```json
{"paths":["scripts/example.pex"],"format":"json"}
```

```json
{"under":["scripts/**/*.pex"],"provider":"Example Mod","patch":"ScriptReview","limit":20,"format":"json"}
```

The second example is for `housecarl_decompile_script`. `provider=` selects scripts; `source_provider=` selects whose copy is read. The native Champollion backend remains optional through `decompiler_backend="champollion"`. Its reconstruction is unverified and must be reviewed before use. Existing `.psc` files are not overwritten.

Asset Walk now shares the record walk's depth, node budget, exclusions and transport grammar. It follows forward dependencies and treats reached NPCs as boundaries by default. Seed an NPC for its FaceGen; seed or reach a dialogue record for its audio. The [Asset Walk guide](asset-walk.md) includes examples. `@file` inputs follow each parameter's declared list grammar.

## Amethyst 2.6.4

Stable [Amethyst 2.6.4](https://github.com/ChrisDKN/Amethyst-Mod-Manager/releases/tag/v2.6.4), commit `6c5d068b904dc0a33f691ab7c847653caff362ea`, retains API 13, schema 10, engine 1 and rules 19. Its native tree is identical to the previously tested 2.6.4 betas (`c4519af475aebf816b348868e10294d47dd5ca2e`), so it uses the existing rules-19 reader. No revision check is removed or weakened.

The producer/matching-reader comparison, explicit connection, saved winner/provenance checks, stale-source and changed-intent refusals, and automatic-refresh lifecycle were tested in isolated synthetic profiles against the published 2.1.5 server. Rules-14/rules-15 readers still fail the expected case-distinct identity counterexample; that is a comparison result, not the selected rules-19 runtime failing.

Changes since beta.4 include Skyrim root routing, Steam detection and launch wrappers, and recovery when game folders disappear. The adapter consumes the producer's saved routes rather than duplicating them. Synthetic filegraph tests do not certify game-specific GUI routing, a live deployment or gameplay.

The headless API still lacks the complete generation-bound resolved snapshot needed to replace this reader. `profiles.export.start` writes a profile package; it does not expose the complete read-only winner/provider and source-identity view. The public headless registry and documentation were unchanged from the tested beta.4 revision. An API adapter refactor remains separate work.

## Validation

The integration is checked in a disposable Linux build environment with private HOME/XDG state. The live profile, Steam mapping, Proton prefix and installed server are not mounted. The compatibility worker reads the candidate's published selector schema so it can test both the older `asset_paths=` releases and the new `paths=` API. Passing tests do not automatically approve an unknown Amethyst contract.

The full C# suite passed 9,345 tests with one existing Windows-only skip. All 35 Python tests passed. The private self-contained 2.2.0 archive passed payload verification, all four native-reader load probes, MCP initialization and discovery of 34 tools. The six new end-to-end tests include actual native Champollion batch execution, managed publication and unrelated-source rejection.

The packaged 2.2.0 server also passed the stable 2.6.4 runtime and automatic-refresh checks described above. Producer inputs remained unchanged outside intentional synthetic lifecycle refreshes. The overall comparison remains `comparison_failed` because the deliberately compared older readers fail; the selected rules-19 reader and runtime both pass.

Architecture analysis used a code-only Graphify corpus of this fork, the pinned houseCARL source and Amethyst backend/filegraph modules, plus a checkout-local Code Review Graph index. Graphify partially parses `SpidLines.cs` and reports a namespace identity collision; CRG's broad change summary is capped at 500 functions and cannot establish reflection-driven record coverage. Findings require source and test verification. Independent review and CI are recorded with the pull request.
