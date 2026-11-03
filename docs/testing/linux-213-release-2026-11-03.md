# Linux preview 2.1.3 release verification, 3 November 2026

The implementation from [PR #20](https://github.com/ni1by2/houseCARL-Amethyst/pull/20) merged at `99f65b9c42a5d2df1769558ab7c5dc57dc52e3af`. Its [final CI run passed](https://github.com/ni1by2/houseCARL-Amethyst/actions/runs/41501409813). The [upstream assessment](upstream-integration-2026-11-02.md) records the source pins, compatibility tests, independent code review and API limitations.

The release cut changes the package manifest to 2.1.3 and updates the current README, install and source-build instructions, snapshot contract and changelog. Application source and dependency pins are unchanged from the reviewed integration. A fresh independent agent reviewed this documentation/version diff and found no actionable defects.

## Package checks

- A clean self-contained, untrimmed Linux x64 publish was built from `56266406`, with SDK 10.0.403 and runtime 10.0.14. The final tag also contains verification documentation; it does not change the application source used for that build.
- Payload validation passed for 691 files. The bundled README and installation guide match the release source. The installer is unchanged from the previous release.
- The archive probe loaded all three API-13 native contracts: schema 9/rules 13, schema 10/rules 14 and schema 10/rules 15. It checks each module's actual rules stamp in a separate empty catalog.
- Packaged MCP initialization reports **2.1.3** and tool discovery returns **34 tools**.
- The dependency-notice check passed against the publish output: **45 components**, both tracked copies consistent.
- Archive SHA-256: `dd8a71f39f8b6e5f14c7e742f8f90a043ec09bd69dcdeae39ecd7d5c787ec7b9`.

The complete integration suite already passed with **7,880 C# tests, zero failures and one existing Windows-only skip**, plus **21 Python tests**. These local suites were not repeated for documentation and version metadata alone. Publication requires the release PR's CI to pass as well. The release page records the final tag and download checksum.

## Upgrade and remaining limits

Use the [2.1.3 installation guide](../linux-install.md), including host shutdown, checksum verification, the required `install --archive` arguments, restart and version/connection verification. Existing connection and transaction state remain outside the program directory. The package bundles .NET 10; users need Python 3.12+ and the listed operating-system libraries.

Version 2.1.3 can read the new rules-15 snapshots from the pinned Amethyst 2.6.2-beta.3 producer. Earlier houseCARL 2.1.1/2.1.2 packages cannot read that contract, so server rollback does not guarantee compatibility with an already-updated Amethyst catalog. Unknown producer contracts still refuse. New independent-instance layouts have not received separate live acceptance, and the headless API refactor remains deferred.

All local checks used isolated state and synthetic inputs. No installed server, host registration, live Skyrim profile, mod order, enablement or deployment was changed. Installation, Proton and gameplay acceptance were not repeated for this release.
