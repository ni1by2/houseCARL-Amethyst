# Amethyst compatibility-suite verification, 8 November 2026

The suite was developed against houseCARL-Amethyst 2.1.3 at `18d157912967bcb071f8add32cb54b90624dfa7d`. It changes test infrastructure and documentation, leaving application source, supported revisions, installed packages and live profiles unchanged.

The fixed reader/server baseline was extracted from the verified 2.1.3 archive, SHA-256 `dd8a71f39f8b6e5f14c7e742f8f90a043ec09bd69dcdeae39ecd7d5c787ec7b9`. Only the new rules-18 producer component was compiled, from Amethyst 2.6.3 at `788a71262ca479b6cbccc53ff3a0083250e3833c`, with locked/offline dependencies on the existing Ubuntu baseline. Existing reader binaries were reused.

## Executed matrix

| Producer source | Contract | Same-schema native comparisons | Packaged MCP result |
|---|---|---|---|
| `edde06e518c0a3ca5ba47a8bbf065c4ecc4a8f92` | API 13/schema 9/engine 1/rules 13 | Producer control and bundled rules-13 reader passed. | Accepted; status, asset/provenance, changed-source and changed-intent refusal passed. |
| `abeae84afce940c6819f80c1cd4e6e51db2e935d` | API 13/schema 10/engine 1/rules 14 | Producer control and bundled rules-14/rules-15 readers passed. | Accepted; the same read/refusal checks passed. |
| `7de35eaf0c7191defa7aee33d61452ddf73f7da3` | API 13/schema 10/engine 1/rules 15 | Producer control and bundled rules-14/rules-15 readers passed. | Accepted; the same read/refusal checks passed. |
| `788a71262ca479b6cbccc53ff3a0083250e3833c` | API 13/schema 10/engine 1/rules 18 | Producer control and bundled rules-14/rules-15 readers passed. | Blocked by the existing production contract check. |

All four runs used the documented minimal compatibility image. Each compared ten winner identities and ten provider chains, saved generations and the uncommitted-deployment check. Producer inputs retained their data hashes. Readers with a different schema were recorded as `not_applicable`, not counted as parity passes. Every report retained `approved:false`.

The current producer fixture verified that conflict-only filename/folder rules retain winning, deployable assets while suppressing their conflict status. Its genuine exclusion and disabled-mod cases remained absent. Archive, root and overwrite cases were included. The older readers' matching answers are evidence for investigating a shared-reader policy; they do not establish general forward compatibility or authorize a runtime policy change.

The initial Python suite passed **26 tests**, including five new harness tests. The new tests reject changed winner/source identities, truncated or unbounded frames and dirty upstream sources, distinguish schema mismatches from parity passes, and ensure nonempty WAL/catalog changes remain detectable. SQLite read-created empty WAL and shared-memory lock files are distinguished from catalog data.

Independent review identified two reporting cases: Docker startup failures must use the documented setup exit code, and a future API/schema beyond the baseline helper's transport coverage must not be classified as a native parity regression. Both were corrected with negative tests, including an unsupported control followed by a server failure. Producer-control failures now leave the experiment as a setup failure; reports retain the raw Docker exit separately.

After those corrections, all eight focused harness tests passed. The unchanged installer/package tests had also passed in the preceding full Python run; PR CI checks the final combined suite.

Initial harness runs exposed an incorrect paging assumption, missing Data prefixes in fixture assertions and an uncreated disposable HOME for .NET mutex initialization. Those harness issues were corrected before the complete matrix above. A zero-byte read-created SQLite WAL was recorded explicitly before normalizing that non-data sidecar; nonempty WAL changes remain a failing check.

## Scope

The [usage guide](../amethyst-compatibility-testing.md) describes local runs, the input-ref workflow, report fields and exit codes. JSON reports retain exact source/binary/probe hashes and server admission independently of native parity. Setup failures and failed comparisons remain distinct.

This does not certify managed writes, transaction races, every archive/handler case, real deployment, Proton or gameplay. Existing adapter and transaction suites remain required when changing production compatibility policy. No local C# rebuild or duplicate full C# run was needed for these Python/test-documentation changes; the repository's PR CI remains the required build/test check before merge. No release or automatic compatibility approval is included.
