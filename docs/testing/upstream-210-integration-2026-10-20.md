# Upstream 2.1.0 integration — 20 October 2026

This is a source integration candidate, not a published Linux release or a live-profile installation.

The integration starts at `amethyst-main` commit `85af49e43feb5180b5532899f4c93bc54f1caa5b`
and merges upstream houseCARL 2.1.0 at `1391dacdb399f25436142507b9e7763b3059a472`.
It retains the Amethyst native component pinned to `edde06e5` (API 13, schema 9,
engine 1, rules 13). Amethyst beta.3 does not require a native contract update;
headless manager-client work remains separate.

The merge preserves Amethyst identity and staged transaction publication while
adding upstream's source-change checks. The private compact/merge scratch phase
is excluded from published-output accounting. Actual transaction and local
publication boundaries perform the freshness check and record committed paths.
Regression cases cover a competing save before an Amethyst commit and a successful
journaled write that is not mistaken for an external save. The imported Linux
fixtures normalize physical FaceGen paths and accept POSIX artifact paths. BSA
script namespace caching parses virtual archive separators independently of the
host operating system.

Validation uses an Ubuntu 24.04 container with .NET SDK 9.0.316 and Python 3.12,
as UID 1000 with networking disabled during build/test execution. Missing
self-contained runtime packages were restored separately into a disposable private
NuGet cache before the offline archive build. Configuration,
state, cache and temporary files stay in disposable storage. The native reader,
Caprica and Champollion are reused unchanged from the verified 2.0.5 package;
this integration does not rebuild those pinned native inputs.

## Results

- The whole Release solution build passed. Compiler/analyzer warnings remain.
- The focused regression set passed: 146 tests, no failures.
- The complete Linux C# suite passed: 7,350 passed, one Windows-only skip, no failures.
- All 21 Python installer/package tests passed.
- The final self-contained archive was built from runtime/source commit
  `37c9b0eaeb9d8a40c968a4b392277cead429b527` and passed payload hashes,
  native API 13/schema 9 loading, MCP initialization/version 2.1.0 and the 34-tool list.
  The seeded stale-output sentinel was removed.
- Archive SHA-256: `214235c588d0ddb1968530f772df8a8dd8cab3e8506df6a1f8e7c32456f161e7`.
- This is a local validation candidate, not a published release. The earlier
  `1307ece5` archive is superseded and must not be installed or published.
- The parent-replacement regressions were checked against the previous ordering:
  both failed there and passed with source validation followed by the final parent check.

An initial full run exposed Windows-specific physical FaceGen fixture paths,
a Windows-only artifact-path matcher, and host-dependent BSA namespace parsing.
The fixes above were followed by the passing full run. No previous release result
is claimed as evidence for this changed source.

## Limits

No installed runtime, live Skyrim profile, mod ordering, enablement, deployment,
Steam compatibility mapping or prefix was changed. Live deployment and gameplay
remain untested. Independent review is required before landing this candidate.
The published 2.0.5 tag and downloads remain unchanged.
