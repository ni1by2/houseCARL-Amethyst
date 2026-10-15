# Testing

Tests live in `src/housecarl-mcp-tests` (xUnit). CI builds the solution and runs this project.

## Two kinds of test

- **Engine tests** call a tool's C# entry point (for example `RecordsTools.Records(...)`) against a synthetic MO2 instance and assert on the response.
- **Wire tests** drive the built `housecarl-mcp.exe` over stdio through `ServerFixture`, so they see exactly what a caller sees: the published tool list, the schemas, the bound parameters.

Most tests are engine tests. Write a wire test when the thing under test is the publication itself: a tool's name, its schema, or whether a call reaches the tool body.

## Worlds

A world (`RecordsWorld`, `ScriptsWorld`, `DialogueWorld`, `CheckErrorsWorld`, `ArtifactWorld`, `BulkRecordsWorld`) builds a temporary MO2 instance with real plugins written by Mutagen: a master, overrides at different load positions, a disabled patch, scripts. One instance is shared through an xUnit collection fixture or class fixture. A test that mutates the world (rewrites a plugin, touches an mtime) builds its own instance so it cannot poison the shared one.

To test a new behaviour, first look for a world that already has the records you need. Add to one before creating another.

## Writing a test

1. Arrange with the world: pick the record, the plugin, the state.
2. Act by calling the tool once.
3. Assert on what the caller would look for: the record that should appear, the value it should carry, the refusal it should get. Most test bases have `Served(response, ...)` and `Refused(response, ...)` helpers for the common cases.
4. Tag the class with a tier trait: `[Trait("tier", "unit")]` when it needs no world, `"integration"` when it drives a world, `"stdio"` when it goes through `ServerFixture`. A test that needs a tool CI lacks (the Papyrus compiler) uses a fact attribute that sets `Skip` when the tool is absent, so it reports as skipped, not passed.

Assert the behavior the caller needs. For a refusal, check the relevant parameter or rule so changes to surrounding wording do not break the test.

A regression test must fail before the fix and pass afterward. Delete tests that cannot fail. Give each test one behavior and a name that describes it, with setup a reviewer can follow.

## What not to write

- No tests about tests: no guards over test files, no baseline counts, no sweeps that check the suite's own shape. The few that still exist are being deleted; do not add to them. If you doubt the suite, run Stryker.NET once, fix what it shows, and move on.
- No test that needs the real game, unless it skips when the external tool or the install is absent. `PapyrusCompileEndToEndTests` needs real native Caprica but uses authored sources, not an installed game; `CapricaFact` reports an absent tool explicitly. Build the native tools or set `HOUSECARL_TEST_CAPRICA` to run these cases. Anything that needs a real load order runs locally; the PR says what was run and what it showed.
- No duplicate of a probe. The probes left in `src/housecarl-generator` are manual, run by name; none runs on CI. When you change what one covers, move that coverage here and delete the probe.

## Running

```
dotnet build housecarl.sln -c Release
dotnet test src/housecarl-mcp-tests -c Release --no-build
```
