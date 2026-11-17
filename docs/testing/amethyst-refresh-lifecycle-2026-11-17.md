# Automatic filegraph-refresh suite update, 17 November 2026

The maintainer suite now tests Amethyst's filegraph-version marker transitions in a separate disposable profile. Its expected marker comes from the supplied producer's schema, engine and rules constants. No application-version list or runtime compatibility policy is changed.

The new subprocess verifies that refresh records the current marker, matching markers avoid refresh, and missing or old markers each refresh once and then settle. After reconciliation it checks that an uncommitted deployment is still unverified, then runs the packaged server's connection, status, winner/provenance and changed-source/intent refusal checks again. Explicit contract refusal remains visible for historical servers. Reports retain this result separately from the original fixture's unchanged-input checks.

## Local verification

All 34 Python tests passed, including refresh-failure propagation, missing/malformed subprocess reports, exit-code disagreement and timeout classification. The unchanged C# binaries and native components were reused; there was no local release rebuild.

The updated suite was exercised against these clean upstream sources, using the published, checksum-verified houseCARL 2.1.4 server unless stated otherwise:

| Producer | Commit | Result |
| --- | --- | --- |
| Schema 9 / rules 13 control | `edde06e518c0a3ca5ba47a8bbf065c4ecc4a8f92` | Comparisons complete; runtime accepted; refresh feature explicitly not applicable |
| Schema 10 / rules 14 control | `abeae84afce940c6819f80c1cd4e6e51db2e935d` | Comparisons complete; runtime accepted; refresh feature explicitly not applicable |
| Schema 10 / rules 15 control | `7de35eaf0c7191defa7aee33d61452ddf73f7da3` | Comparisons complete; runtime accepted; refresh feature explicitly not applicable |
| Stable 2.6.3 / rules 18 | `788a71262ca479b6cbccc53ff3a0083250e3833c` | Comparisons complete; runtime accepted; refresh feature explicitly not applicable |
| 2.6.4-beta.4 / rules 19 | `0b4e2899e702320f3e9c7ed5fa81f973c4d16330` | Producer control, matching reader, runtime checks and refresh lifecycle pass |
| Same beta.4, fixed 2.1.3 baseline | `0b4e2899e702320f3e9c7ed5fa81f973c4d16330` | Refresh lifecycle passes; post-refresh runtime explicitly blocked by its older contract |

All original producer input-integrity checks passed. Each beta.4 overall report remains `comparison_failed` because the intentionally tested rules-14/rules-15 readers return different winner identities in the case-distinct archive fixture. The 2.1.4 mapped rules-19 reader passes. The 2.1.3 baseline has no rules-19 reader and refuses the contract. These results do not justify selecting an older reader or weakening runtime admission.

Amethyst's entire native filegraph tree, including Cargo.lock, is identical between beta.1 and beta.4 (`c4519af475aebf816b348868e10294d47dd5ca2e`), so the validated rules-19 producer extension was reused. The actual beta.4 Python service supplied the new refresh behavior. The workflow's fixed 2.1.3 baseline and existing control matrix are unchanged; a manual candidate run against beta.4 also records its explicit runtime block and expected older-reader failures.

## Scope and retained evidence

The new `refresh-lifecycle.json` and `report.json` fields distinguish `pass`, `not_applicable`, behavioral `fail` and driver `setup_error`. A lifecycle failure cannot produce `comparison_complete`; a driver failure returns setup exit 2. Every report remains `approved:false`. Unsupported producer changes may still require fixture-driver work; absence of the marker API claims no lifecycle coverage.

Local JSON reports, container logs and binary/probe hashes are retained under `Documents/Codex-references/upstream-analysis/2026-11-17/suite-evidence/`. The earlier source/API assessment is beside that directory. Upstream's public snapshot API is unchanged; no snapshot API refactor is included here.

Tests ran in offline containers with private HOME/XDG directories and read-only source/server mounts. Intentional refresh writes went only to disposable synthetic profiles. No live Amethyst profile, installation, Skyrim game/prefix, mods, deployment or host registration was changed. These checks do not certify managed writes, deployment races, Proton or gameplay.
