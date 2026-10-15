# Changelog fragments

A PR that a user would notice adds its changelog entry here as its own file, `plugin/changelog.d/<branch>.md`,
named after its branch without the `claude/` prefix (`claude/fix-1234` writes `fix-1234.md`). Nothing goes
under `## Unreleased` in `../CHANGELOG.md`, so open PRs never conflict on one shared section. If `<branch>.md`
already exists, name yours `<branch>-2.md` (then `-3`, and so on); never edit another PR's fragment.

Write the entry as it should appear in the changelog: a `- ` bullet with one bold sentence saying
what the tool now does or refuses, and at most one more saying how to check it, under about forty words. Describe
what users notice and how to check it; how it works, contracts, caveats and edge cases go in the
matching `docs/architecture/` note.

The release cut runs `scripts/fold-changelog.ps1 -Version <ver>`, which writes every fragment, in merge order,
under the new version heading and deletes them. `scripts/build-plugin.ps1` ships them under `## Unreleased`.
