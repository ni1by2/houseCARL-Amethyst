# Contributing to houseCARL

houseCARL accepts issues and pull requests.

## Before you start

For changes beyond a typo fix, open an issue describing the problem or proposal before you implement it. Maintainers can confirm the direction or point you to a fix already on `main`.

## Bug reports

Include the following in a bug report:

- The houseCARL version. Fixes may already be on `main` awaiting release.
- The tool calls and full error or incorrect output. Stderr from a direct stdio run is useful when available.
- Relevant environment details: Amethyst version, Linux distribution, Skyrim SE runtime, and load-order size.

## Pull requests

- Open the PR from a branch on your fork and target `main`.
- Keep each PR to one logical change so it can be reviewed on its own.
- Link the issue using `Fixes #123` (or `Closes #123`) in the description so GitHub closes it when the PR merges.
- For a change users would notice, add `plugin/changelog.d/<branch>.md` in the same PR. See the README in that directory. The entry says what a user notices and how to check it; how it works, contracts, caveats and edge cases go in the matching `docs/architecture/` note. `plugin.json` stays untouched until an actual release.
- The `build + probes` CI check must pass before merge. GitHub requires maintainer approval before running CI for first-time contributors.
- Keep your branch rebased on current `main`; PRs are merged by rebase.
- Include a test for each fix in `src/housecarl-mcp-tests` that fails before the change and passes after; see `standards/TESTING.md`. Anything that needs real game data runs locally, with what you ran and saw noted in the PR.
- Keep record-type support generic. Support is generated from Mutagen's model, never hand-written per type, and bulk work composes from a small set of generic verbs, never a one-off tool for one job. A change that special-cases one record type, or hard-codes one job's domain knowledge into a tool, will be asked to generalize (domain knowledge belongs in skills as data).
- Report unsupported operations with a named error that explains what was checked and what to try next. Do not return a wrong answer, an unexplained miss, or silently degraded results.
- MCP tools are `housecarl_<snake_case>`; see `standards/NAMING.md`.

## License

houseCARL-Amethyst inherits GPL-3.0-only from upstream houseCARL. By submitting a pull request, you agree to license your contribution under GPL-3.0-only. See the repository README for the licence chain and redistribution requirements.
