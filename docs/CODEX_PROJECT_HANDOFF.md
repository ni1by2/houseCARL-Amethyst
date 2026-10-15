> Historical record: this describes the earlier filemap-based build or its migration plan. For current setup and limitations, start with [README](../README.md) and [Linux installation](linux-install.md).

# houseCARL-Amethyst — Codex project handoff

**Prepared:** 2026-10-02. **Purpose:** move work into a dedicated Codex project without losing the fork's design, current evidence, or safety boundaries. This is a handoff and a proposed update sequence, **not** authorization to port code or deploy a game profile.

## Start here

The [roadmap](HOUSECARL_AMETHYST_ROADMAP.md) is the durable implementation record, but its July 2026 assumptions about Amethyst's filemap and the local deployment are stale. Read this handoff first, then [AGENTS.md](../AGENTS.md), [CLAUDE.md](../CLAUDE.md), the [README](../README.md), and the [code documentation standard](../standards/HOUSECARL_CODE_DOCUMENTATION.md). Some `CLAUDE.md` references point to upstream's private, unavailable `dev/` corpus; do not invent its contents. Recheck all live state before acting.

The local Git checkout, not just GitHub, is the working copy. As observed on 2026-10-02, it was on `agent/upstream-c305b07-amethyst` at `b7a101dd92e155a3f30de56a138b8879e5d1dc97`. The published fork's `amethyst-main` pointed to that same commit at the last successful remote check. The worktree also contained an unrelated, untracked `.agents/` directory: preserve it. `origin` is [ni1by2/houseCARL-Amethyst](https://github.com/ni1by2/houseCARL-Amethyst); `upstream` is [Avick3110/houseCARL](https://github.com/Avick3110/houseCARL). Verify branch heads again because these are dated observations, not live guarantees.

## Product contract that must survive the update

- Linux x86_64, Amethyst only, self-contained .NET server, Codex and Claude Code hosts. No Amethyst-side connector or plugin: the fork ships a standalone connection/setup command.
- Keep Mutagen reflection-driven coverage, fail-loud semantics, Bethesda-versus-native path separation, and non-destructive default patch mods. Keep code concise and auditable while documenting every declaration and non-obvious invariant in plain English.
- Read active profile and staging state from Amethyst; never infer winners by scanning merged deployed `Data/`, and never silently fall back from an invalid Amethyst snapshot.
- Hardlink deployment is the v1 release requirement. New patches go to staging and require Amethyst refresh/enable/deploy. In-place edits retain per-plugin consent, require explicit redeployment confirmation, write only to staging, and remain pending until a later deployment is verified. Symlink/copy smoke tests are opportunistic; VFS is not a v1 target.
- No copyrighted Skyrim data in CI. A disposable real-game hardlink validation remains a separate manual release gate.

## Upstream audit snapshot and correction to the roadmap

The 2026-10-02 **read-only** audit compared each upstream repository's `main` branch. It did not merge, edit code, run a new test suite, or deploy anything. Refresh the commit pins before implementing; this snapshot is an input to the next review, not a claim about future `main` heads.

| Repository | Audited `main` commit | Main finding |
| --- | --- | --- |
| [houseCARL](https://github.com/Avick3110/houseCARL) | `9d38d889976f4cadaa30a6176a43dd05456dbefc` | The upstream 2.0 tool surface, dependencies, and test organization diverged substantially from the fork. |
| [Amethyst Mod Manager](https://github.com/ChrisDKN/Amethyst-Mod-Manager) | `c0e650b6790e81b0641f31e354397d140b32428e` | The live resolver now uses a SQLite-backed `filegraph`; the fork still requires old `filemap.txt` plus MessagePack `modindex.bin` v4. |

The fork roadmap's houseCARL pin `7904854708c15a8afec0d256037aee3b49e5a336` is invalid. The audited shared-base commit was `79048545acf19b85eed60c901ba48f93285fec28`. Correct the roadmap as part of the next implementation checkpoint; retain the bad value in history only as an identified documentation error.

The fork registers 46 older MCP tools. Upstream 2.0 uses 31 composed tools, and its alias table explains retired calls rather than executing them. The fork is also behind audited upstream package versions (Mutagen.Bethesda.Skyrim 0.53.1 versus 0.54.4, Nifly 1.0.0 versus `1.1.0-housecarl.4`, and ModelContextProtocol.AspNetCore 1.3.0 versus 2.2.0). A dry-run merge analysis reported 196 conflicts; do not blindly merge into `amethyst-main`. These counts and versions must be rechecked against newly pinned heads.

The Amethyst change is a functional blocker, not merely a filename change. Its [filegraph schema](https://github.com/ChrisDKN/Amethyst-Mod-Manager/blob/c0e650b6790e81b0641f31e354397d140b32428e/native/amethyst_filegraph/src/schema.rs) and [service](https://github.com/ChrisDKN/Amethyst-Mod-Manager/blob/c0e650b6790e81b0641f31e354397d140b32428e/src/Utils/filegraph/service.py) hold winner, source, generation, and deployed-entry information. The [CLI filemap export](https://github.com/ChrisDKN/Amethyst-Mod-Manager/blob/c0e650b6790e81b0641f31e354397d140b32428e/src/cli.py) is a compatibility export and does not by itself restore the raw-cased source mapping the fork needs. First prove a read-only, version-checked snapshot contract; if the database cannot supply a trustworthy stable view, propose a versioned full CLI export to Amethyst upstream instead of guessing.

Amethyst `main` also has a real Skyrim SE VFS deployment backend. The roadmap's claim that Amethyst has no VFS backend is outdated. This does **not** expand the fork's v1 scope beyond hardlinks. Amethyst can fall back from requested hardlinks to other link modes, so deployment proof should inspect the actual per-file mode and identity rather than trusting a requested mode label.

## Proposed implementation sequence — no code work started

1. **Rebaseline.** Confirm local and remote heads, pin both upstream `main` commits, inventory the existing diff, correct stale roadmap facts, and run the existing Linux probes before changing behavior. Preserve `amethyst-main` as a recoverable baseline.
2. **Prove current Amethyst state resolution.** Build synthetic fixtures for shared/profile-specific staging, case-sensitive paths, profile changes, exclusions, overwrite, and deployment generations. Define a read-only filegraph adapter with explicit schema/version, ready-state, profile, winner/source, and generation checks. Refuse partial or stale views.
3. **Forward-port the engine.** Use a separate integration branch based on pinned houseCARL `main` and carry the Linux/Amethyst boundaries forward in small, reviewed commits. Retain reflection-generated record coverage and safety guards; update package versions and adapt to upstream's current tests. Do not duplicate old and new engines wholesale.
4. **Resolve the public MCP interface.** Product decision required: adopt the upstream 2.0 composed tool names with migration guidance (recommended for a sparse fork), or deliberately maintain executable compatibility aliases. Keep Amethyst connection, status, refresh, and redeployment safeguards either way.
5. **Rework deployment proof and user guidance.** Replace old filemap-timestamp assumptions with filegraph generation, committed deployment state, and staging-versus-deployed inode/content checks. Update skills, installer text, README, troubleshooting, and roadmap, including the corrected VFS statement.
6. **Verify before release.** Run strict Linux build, current upstream tests and fork-specific synthetic probes, self-contained packaging, Codex/Claude startup checks, and leak scanning. Only then request a separate go/no-go for a real disposable-profile hardlink deployment; record rollback evidence first.

Each behavioral change needs a failing-before/passing-after probe. Keep upstream merges separate from fork porting commits, document decisions and residual risk in the roadmap, and publish only verified checkpoints.

## Live Amethyst safety boundary

The July roadmap says the normal profile was active and hardlink-deployed. A later **read-only** local check on 2026-10-02 instead found deployment inactive, although its last recorded mode was `HARDLINK`. An isolated profile-specific test profile had been created earlier but was not selected or deployed. Therefore the July rollback hashes and active-state claim cannot authorize a deployment now. Do not change the current profile, stage a test mod, rebuild/deploy, or touch game `Data/` on the strength of this document. Re-read the live Amethyst configuration, make fresh backups and rollback evidence, explain game-visible effects, and obtain explicit confirmation at that boundary. Keep machine-specific profile paths and load-order details out of the public repository.

## Evidence, unknowns, and first Codex-project task

The last recorded hosted green run is [GitHub Actions run 30221301116](https://github.com/ni1by2/houseCARL-Amethyst/actions/runs/30221301116), for July commit `9ec2538`: strict .NET 9 build, 111/111 then-current CI probes, cold freshness guard, and self-contained Linux packaging. This is **historical evidence**, not proof against the October upstream/Amethyst heads. The precise installed Amethyst build, latest remote heads after this audit, filegraph compatibility, and real hardlink behavior remain unverified.

For the new Codex project, open this local repository as the workspace. Ask it first to perform a **read-only** rebaseline of fork, houseCARL `main`, and Amethyst `main`, and to propose a corrected, testable filegraph contract. It should report any changed evidence and the MCP-interface choice before editing code. The user's last instruction for the upstream comparison was explicitly **plan, not execute**; this handoff does not supersede that limit.
