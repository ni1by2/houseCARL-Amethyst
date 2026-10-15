# Generated-report publication race — 2026-10-15

## Failure and reproduction

At `f4481c4b`, the push CI run failed `RecordsArtifactTests.AnAutoSpillLandsWhileAScannerGrabsEveryFileTheResultsDirectoryGains`: the results directory contained 143 JSONL files when one complete report was expected. Another run on the same commit passed.

The cause was the interval between .NET creating a new file and obtaining its advisory lock on Linux. An indexer could take a read lock first. `ResultsStore.Reserve` caught the resulting `IOException`, saw that the newly created file existed, treated it as somebody else's filename collision, and retried. The abandoned empty file remained.

The diagnostic interposer at `src/housecarl-mcp-tests/fixtures/artifact-publication/force-scan-race.c` pauses the first report's exclusive lock until a scanner has acquired its read lock. Running the original test on `f4481c4b` with that process-only interposer reproduced the same assertion failure with **two files**. No source mutation or Amethyst profile was needed.

Compile the interposer with `cc -shared -fPIC -o /tmp/force-scan-race.so src/housecarl-mcp-tests/fixtures/artifact-publication/force-scan-race.c -ldl`. Set `LD_PRELOAD=/tmp/force-scan-race.so` only on the filtered `dotnet test` process. The interposer is a diagnostic fixture, not runtime or release code.

## Change

Generated reports now choose a unique final name without creating it. The writer creates and owns a new sibling scratch inode using native Linux `open`, wraps that descriptor without requesting a later advisory lock, writes and flushes all bytes, then publishes without replacing an existing destination. Failures clean up owned scratch files; competing final files and symlinks remain untouched. Caller-named replacement writes retain their existing atomic-commit path.

The scanner fixture now observes both creation and rename events. A per-test publication barrier requires it to hold the temporary file **before** publication. Its existing one-file and manifest-content assertions remain. This also corrects a fixture timing failure seen in the first full run after the publisher change: creation-only events could refer to a temporary name that had already been renamed.

Eight added test cases cover visibility while writing, partial-write and pre-publication failure cleanup, a scanner holding the scratch inode across publication, competing files and symlinks, unwritten-target disposal, and 32 concurrent writers. The two initial regressions failed against the old implementation. Existing reservation tests now assert that no incomplete final report exists, replacing their earlier file-sharing assumption without losing the protection being tested. No tests were skipped or deleted for this fix.

## Validation

- Before the fix, the original scanner test failed under the timing interposer, leaving two reports. The first two new regression tests also failed against the old publisher.
- After the fix, all 16 focused tests passed. The scanner test passed eight consecutive subprocess runs under the same interposer while the full suite ran.
- The Release build succeeded. The complete .NET suite passed: **7,150 passed, 0 failed, 1 skipped** (7,151 total). The existing skip exercises Windows file-locking behaviour and remains inapplicable on Linux.
- All **21 Python installer and packaging tests** passed. `git diff --check` passed.

Windows execution remains unverified. All runs use synthetic temporary data; no live mods or deployment state were changed. Upstream compiler warnings were outside this fix's scope.
