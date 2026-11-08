# Testing a new Amethyst revision

The compatibility suite compares a supplied Amethyst producer against the native readers and MCP server from a houseCARL release. The same suite can run against a new upstream tag or commit without changing the adapter's revision check. It produces evidence for a compatibility decision; it never approves a new contract or changes the installed server.

The GitHub Actions workflow **Amethyst compatibility experiment** accepts `amethyst_ref`. It runs three existing producer controls and the requested revision, preserving JSON reports and logs as artifacts. It is manually triggered, with a pull-request check for changes to the suite. There is no scheduled watcher, automatic merge or release job.

## Run locally

Use Linux x64 with Docker, Git and Python 3. A new output directory is required for each run. Build the small Ubuntu 24.04 runtime image once:

```sh
docker build -t housecarl-amethyst-compatibility \
  -f scripts/probes/amethyst-compatibility.Dockerfile scripts/probes
```

Check out the desired Amethyst tag or commit in a separate clean source tree. Build its native component using that revision's documented build procedure. Supply the resulting `libamethyst_filegraph.so` or `amethyst_filegraph.abi3.so`, plus the complete `server` directory extracted from a verified houseCARL release:

```sh
python3 scripts/probes/run_amethyst_compatibility.py \
  --source /absolute/path/to/Amethyst \
  --producer-extension /absolute/path/to/producer/amethyst_filegraph.abi3.so \
  --server /absolute/path/to/verified-package/server \
  --output /absolute/path/to/new-comparison-report
```

Bundled native readers are discovered from that server directory. Add `--reader NAME=/absolute/path/to/reader.so` for another candidate reader. `--image` selects an equivalent prebuilt test image. The workflow fixes the reader baseline to the checksum-verified 2.1.3 Linux release, while local runs can select a later verified package explicitly. Updating that baseline is a deliberate houseCARL release decision, not a change required for each Amethyst update.

The host verifies that Amethyst's `src` and `native` trees match their commit. It then mounts source, readers and server read-only in an offline container with disposable HOME/XDG directories. Only the new report directory is writable on the host. The suite never opens an installed Amethyst profile, registers an MCP host or installs a package. Upstream fixture code executes inside that container. Downloading dependencies and compiling the producer happen separately; use a disposable builder for those steps too.

## What is compared

Amethyst's actual Python service and native extension generate a bounded Skyrim-like catalog. Expected results come from the producer's export and provider APIs. Exported winners must also match cursor-paged winners. Fixture assertions check mod priority, overwrite precedence, disabled mods, excluded folders, archive members, preserved casing inputs, root-folder routing and modern root strip prefixes. Current producers also exercise conflict-only filename/folder rules: the files remain winning and deployable while their conflict status is zero.

Each reader receives a separate SQLite backup. The suite runs the release's unchanged `amethyst_snapshot.py` transport against it and compares every consumed winner identity, saved generation and inventory generation. It separately compares native provider chains and the uncommitted-deployment verification result. It records the reader's own rules stamp without requiring it to equal the producer's stamp. That intentional experiment happens outside the C# runtime admission path and does not weaken it.

The packaged MCP server is tested separately. For an admitted contract, checks cover explicit connection selection, status, an asset winner with snapshot provenance, refusal after a source file changes, and refusal after profile intent changes. An unsupported contract is recorded as `blocked_by_contract`. Other MCP failures fail the comparison. The original producer files and catalog must retain their hashes; SQLite shared-memory locks and empty read-created WAL sidecars are not treated as catalog data. Nonempty WAL data remains protected.

## Read the report

`report.json` contains source and binary hashes, producer contract, per-reader results, production admission, input-integrity checks and limits. `producer-oracle.json` retains the synthetic expected data; `container.log` retains failures. The request records the suite commit and hashes of the actual probe files, including uncommitted development edits.

| Result | Meaning |
|---|---|
| Reader `pass` | Compared native winner identities, provider chains and the bounded deployment check match the producer. |
| Reader `not_applicable` | API/schema differs; that pair has not passed parity. |
| Reader `unsupported_transport` | The baseline helper cannot exercise that API/schema; native parity was not tested. |
| Reader `fail` | A reader failed or returned different results. Inspect its diagnostic. |
| Server `accepted` | The baseline server admitted the producer and passed the listed read/refusal checks. |
| Server `blocked_by_contract` | Its production revision check refused the catalog; native parity is reported independently. |
| Overall `setup_error` | The environment, producer fixture or producer control could not validate the experiment, including an API/schema outside the baseline helper's coverage. This is not proof of an adapter regression. |
| Overall `comparison_failed` | A reader or server check failed, or producer inputs changed. |
| Overall `comparison_complete` | The experiment completed without failed comparisons. It does not grant compatibility approval. |

Exit codes are 0 for a completed experiment, 1 for a failed comparison and 2 for setup failure. The report retains Docker's raw exit separately as `container_exit_code`. Every report sets `approved:false`, including successful runs with blocked production admission. A green workflow must not be used as automatic permission to relax runtime checks.

## Limits and the next compatibility decision

The suite tests synthetic, bounded inputs. It does not certify all game handlers, every archive ordering case, real deployed state, managed writes, transaction-race rollback, Proton or gameplay. Existing C# tests remain responsible for the adapter's broader safety contracts. Unknown source/API changes can require an update to the fixture driver; such failures remain visible as setup failures rather than silently skipping coverage.

Before accepting a new producer revision, review source changes and these reports, then run the affected adapter and transaction tests. Where parity holds, one native reader may support multiple verified producer revisions. This suite supplies the evidence for that policy change; it does not implement the policy.
