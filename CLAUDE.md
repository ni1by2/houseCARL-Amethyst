# houseCARL

houseCARL-Amethyst is an MCP server that gives Claude Code or Codex access to Skyrim Special Edition records, scripts, assets, runtime layers, and modlist state through Amethyst's saved profile snapshot. The modder works in plain English. The assistant writes new output to staging for review and deployment through Amethyst. Editing an existing plugin in place requires per-plugin consent.

It is one C# process built on Mutagen. Users start at [README.md](README.md). This file is for whoever is changing the code.

> `dev/` is a private working corpus (product PRFAQ, tool-surface spec, backlog, decisions). It is gitignored, so links into it do not resolve in a clone. A session opened as the advisor boots from `dev/advisor.md` before anything else in `dev/`.

## Design

- **Record coverage is generated, never hand-written.** A build-time generator walks Mutagen's record interfaces and emits the schema and validation data. The set of record types houseCARL handles is the set Mutagen models. Do not add a per-record-type schema or write mapping by hand. Report gaps between Mutagen and xEdit explicitly; do not invent coverage.
- **One grammar, closed under composition.** Every operation, single or bulk, is one call composed from a small set of orthogonal axes: select a set of records (one is a set of one), project what to read, apply one of a few generic write verbs. There is no verb per job and no single/bulk tool pair. A new need lands as a value on an existing axis; if it truly cannot, add one general primitive, never a job-shaped tool. The surface contract is `dev/projects/tool-surface-2.0/SPEC.md`; look the verb set up there rather than reciting it. Domain knowledge (field bundles, forbidden prefixes) lives in skills as data.
- **Errors are one plain sentence: what went wrong and what to try.** A tool never returns a silently wrong answer or a silently degraded mode. When it cannot do the thing, it says so.
- **Reads are lazy, freshness is cheap.** Records parse on access from a binary overlay; nothing holds the load order in memory or keeps plugin file handles open at rest; a change on disk is picked up by a last-write-plus-size check. The server runs as one process, with no daemon or live manager tracking.

Code comments refer to the first two rules as the cornerstones. A design question that these four do not settle goes to the PRFAQ (`dev/PRFAQ/`, the problem statement and press release first) and then to Aaron. If something blocks you, say so before working around it.

## How to work

1. **Branch first.** `main` is read-only. Work in a worktree at `.claude/worktrees/<name>` on branch `claude/<name>`, and run every command from inside it: a build, test, or review run from the primary checkout reads `main`, not your branch. Check `git branch --show-current` before the first edit.
2. **Build and test.**
   ```
   dotnet build housecarl.sln -c Release
   dotnet test src/housecarl-mcp-tests -c Release --no-build
   ```
   Tests drive the built server. How to write one: [standards/TESTING.md](standards/TESTING.md).
3. **Commit small.** One change per commit, plain imperative subject under 72 characters. Temp files go in the session's own scratchpad directory; the shared temp directory, under any spelling, is where parallel sessions overwrite each other.
4. **Open the PR.** A paragraph saying what changed and why, `Closes #N` for the issue, and, if a user would notice the change, its changelog entry as `plugin/changelog.d/<branch>.md` (see the README there): what a user notices and how to check it; how it works, contracts, caveats and edge cases go in the matching `docs/architecture/` note. The advisor assigns a fresh agent to review the PR without the implementation discussion. Do not review your own change in its place. Aaron reviews independently as well. Address supported findings and explain disagreements in a PR reply; Aaron decides which findings require changes. He merges on his word with `gh pr merge <N> --rebase --delete-branch`; then remove the worktree.
5. **Write plainly.** Write comments as one line explaining the action. Use ordinary words in commit messages, PR descriptions, and issues; avoid project jargon and lore.

## Where things live

| Path | What |
|---|---|
| `src/housecarl-mcp/` | The MCP server and tool surface |
| `src/housecarl-mcp/LoadOrderService.cs` | The service's head, fields, and the instance, status, config, and Papyrus source-root lanes |
| `src/housecarl-mcp/LoadOrderHost.cs` | The shared head door every area interface extends |
| `src/housecarl-mcp/AssetLayers.cs` | The assets area: asset status, the SKSE layer, NIF, the SkyPatcher layer scan, place; its own class, `AssetLayers(IAssetHost)` |
| `src/housecarl-mcp/SkyPatcherReplay.cs` | The SkyPatcher replay and the one door that opens its context |
| `src/housecarl-mcp/RecordReads.cs` | The reads area: resolve, batch, poles, walk, cross query, info order; its own class, `RecordReads(IReadHost)`, across this file and `RecordPoles.cs`, `RecordWalk.cs`, `RecordQuery.cs` and `TreeFold.cs` |
| `src/housecarl-mcp/RecordChecks.cs` | The checks area: the errors, scripts, facegen and dialogue sweeps; its own class, `RecordChecks(ICheckHost)` |
| `src/housecarl-mcp/RecordWrites.cs` | The writes area: apply, remove, forward, in-place, output path, copy, create, compact, merge; its own class, `RecordWrites(IWriteHost)`, across this file and `RecordCopy.cs`, `RecordCreates.cs`, `RecordReshape.cs` and `WriteMappers.cs` |
| `src/housecarl-mcp/OutputLocations.cs` | The output area: output folders, plugin locate, `.seq`; its own class, `OutputLocations(IOutputHost)` |
| `src/housecarl-mcp/ServiceResults.cs` | The result records and enums those lanes return |
| `src/housecarl-mcp/TypeLookup.cs` | Shared: the type lookup (`type=` string to getter types), one per service, held by the head as `Types` |
| `src/housecarl-core/` | Record, asset, read, and write engines; the load-order resolver |
| `src/housecarl-generator/` | Build-time schema generator; also the manual probes, run by name |
| `src/housecarl-mcp-tests/` | xUnit tests against the built server |
| `src/housecarl-setup/` | Installer |
| `plugin/` | The shipped Claude Code plugin's manifest, changelog, and notices; skills are copied in at build |
| `.claude/skills/` | Skill sources (`/housecarl:<name>`) |
| `docs/` | Architecture notes and design decisions |
| `standards/` | Testing and naming |
| `dev/projects/tool-surface-2.0/` | The tool-surface charter and spec (private) |
| `dev/BACKLOG.md` | What is next, in order (private) |
| `dev/DECISIONS.md` | Aaron's rulings, one line each (private) |

MCP tools are named `housecarl_<snake_case>`; namespaces, classes, and files are named for what they do, not for the brand. Everything else: [standards/NAMING.md](standards/NAMING.md).

## Don't

- Don't work on `main`.
- Don't hand-write coverage for a record type, or add a tool for one job.
- Don't add a guard, sweep, or process rule to catch a mistake. Fix the mistake; if it recurs, fix the code that allows it.
- Don't work around a block silently. Say what blocks you.
- Don't add a new domain to any of the `LoadOrderService` partials. A new subsystem gets its own file.
- Don't edit `dev/PRFAQ/` or `Housecarl [Legacy]/`. Both are frozen reference.
- Don't spawn a Fable agent. Every session, reviewer, or helper you spawn runs on Opus or lower.
