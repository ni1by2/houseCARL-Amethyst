# Asset dependency walks

These features are on the development branch and are not in the published 2.1.1 preview.

Ask houseCARL to follow an item or NPC to the files it needs. A walk follows record links and named assets, then reads mesh links and compiled script parents. It can reach textures, physics configuration, FaceGen, dialogue audio and script fragments. Each result names the dependency that led to it and its provider, or explains why it could not resolve it.

The read uses Amethyst’s last saved filegraph. Its profile and generation accompany the result. Refresh the selected profile in Amethyst after changing files or settings. A walk does not confirm deployment or what the running game loads.

## Examples

Use `housecarl_asset_status` with these arguments, replacing the example paths and FormIDs:

```json
{"asset_paths":["Meshes/example.nif"],"walk":true,"format":"json"}
```

```json
{"formids":["012EB7:Skyrim.esm"],"walk":true}
```

For a mesh’s immediate links without recursive traversal:

```json
{"asset_paths":["Meshes/example.nif"],"links":true}
```

Search by filename and restrict the results to a provider:

```json
{"under":["**/*example*.dds"],"provider":"Example Mod"}
```

Inspect a named donor, including a disabled mod whose inventory Amethyst has saved:

```json
{"asset_paths":["Meshes/example.nif"],"walk":true,"source_provider":"Example Donor"}
```

`provider` selects files supplied by an active provider. `source_provider` chooses the copy to inspect and can name a saved disabled donor. The result distinguishes files supplied by that donor, files supplied elsewhere, and files absent from the captured view. An unknown donor refuses instead of scanning an unrecorded folder.

## Exclusions and limits

Amethyst’s per-profile file exclusions and routed destinations determine the view. Disabling a file in Mod Files excludes that mod’s copy without deleting it. Game-wide blacklist rules also participate in the saved graph. The port does not apply MO2’s `.mohidden` or root metadata rules over Amethyst’s results.

Record traversal follows the upstream source boundaries, with at most three record-link hops and 2,000 entered records. File limits, unreadable sources, missing links, template loops and omitted archive coverage are reported. Large results can spill to a named artifact. A voice-template loop prevents voice resolution for that walk and produces a note.

A walk is read-only. Its suggested placement set is input for a separate write request. Placing assets remains subject to the existing Amethyst transaction, staging and redeployment rules.
