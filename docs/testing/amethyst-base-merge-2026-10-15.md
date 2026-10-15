# Amethyst base merge — 2026-10-15

This merges `amethyst-main` at `296de41f556c727c3a3b024ce899dcc8b8f8a7ff` into `codex/amethyst-linux-integration`, whose pre-merge head was `32240540b5de356a1895f91c0082e06ae5f0135c`. The shared ancestor is `79048545acf19b85eed60c901ba48f93285fec28`. The base had 93 commits absent from the integration branch; there were 200 conflicted paths.

## Resolution decisions

| Area | Resolution and evidence |
|---|---|
| Record engine and public tools | Retain the newer reflection-generated engine, composed tool surface and schemas. The earlier base's comments and formatting do not replace newer implementations. No record-type maps or job-specific operations are introduced. |
| Saved profile and asset ownership | Retain `AmethystConnection`, `AmethystSavedSnapshot`, the pinned native filegraph reader, and `AssetResolver.BuildSavedSnapshot`. These supersede the earlier MessagePack/filemap reader and staging-directory scans. `AmethystSavedSnapshotTests` covers provider identity, missing/stale sources, profile-specific roots, deployment backups and catalogue races. |
| Staged writes and pending deployment | Retain the transaction coordinator, write scopes and request guards, including existing-file visibility consent and rollback. Do not restore the older timestamp-based redeployment proof. `AmethystTransactionTests` and the synthetic write integrations remain in the suite. |
| Linux paths | Retain current Bethesda/native path separation, native ownership containment, explicit ambiguity refusal and the repaired voice destinations. `PluginPathsTests`, `CompactVoiceCarryTests`, merge tests and saved-snapshot tests retain the byte-level checks. |
| Native Papyrus tools | Retain current Caprica compilation, built-in decompilation, optional Champollion and selected-Proton support. The older base's deferred-tool stubs would remove subsequently implemented functionality. |
| Test migration | Keep the newer xUnit replacements for retired generator probes, including the published test-removal audit. Do not resurrect deleted probes or removed skills. Move the base's native/Flatpak discovery assertions into `AmethystDiscoveryTests`, validating the generated manifest through `AmethystConnection.Open`. |
| Standalone discovery | Carry forward `AmethystDiscovery`, exposed from source by setup's `--connect-only` option. It creates connection paths without activating a profile or installing/registering a host. Filegraph compatibility remains the server's responsibility. |
| Manual generator inputs | Carry forward environment-based native inputs instead of developer-specific Windows paths. Replace `mo2-order` with `amethyst-order <connection.json>` and use the current saved adapter for the manual round-trip probe. No deployed-Data winner inference is introduced. |
| Packaging and CI | Retain the current Python installer, deterministic manifest/hashes, recovery safeguards and native-tool packaging. Preserve the earlier `build-release.sh` entry point as a bridge to `build-linux-package.sh`. Use Linux CI, retaining manual dispatch and fork branch triggers. |
| Plugin identity and guidance | Carry forward fork ownership, native server launch metadata and Amethyst skill guidance while retaining the current version and routing table. Preserve the user-approved main and plugin README files byte-for-byte. Restore the user's mod installation/category rules in `AGENTS.md`. |
| Historical records | Retain the older roadmap, handoff, installation notes and documentation standard with a visible historical boundary. Their bodies remain records of the earlier implementation; current setup and comment conventions are linked separately. |

Some shared helpers and fixture APIs retain upstream MO2 names. They are retained for compatibility with the newer engine and its tests; they are not an alternative source of winners in the Linux saved-snapshot path. This merge does not claim a Windows product or Windows execution coverage.

## Verification

The first complete run had 7,139 passed, one failed and one Windows-only skip. Its sole failure was a skill-inventory check seeing empty directories left by modify/delete conflicts. Those directories contained no retained skills and were removed.

Final verification on Linux:

- Clean Release build: succeeded, zero errors; 68 compiler/analyzer warnings remain.
- Complete xUnit suite: **7,142 passed, zero failed, one skipped (7,143 total)**. The skip exercises Windows file-lock denial; Linux open-inode behavior remains covered separately.
- Python installer/package suite: **21 passed**.
- Standalone setup process: a synthetic `--connect-only` run created the expected manifest and created no MCP host registration or filegraph.
- Main and plugin README bytes match the pre-merge head exactly.
- No unresolved index entries or conflict markers remain. Windows execution was not performed.

No live Skyrim profile, deployed files, host registrations, default branch or release tag were changed by the merge verification. Packaging acceptance on a clean Ubuntu installation and the other documented binary-release checks remain separate.
