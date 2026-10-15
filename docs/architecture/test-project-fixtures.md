---
updated: 2026-09-23
covers: [src/housecarl-mcp-tests/PexWriter.cs, src/housecarl-mcp-tests/ScriptsWorld.cs, src/housecarl-mcp-tests/HeldOpen.cs]
---
# Test-project fixtures: the Papyrus world and the file-lock harness

## What it is

The test project has a Papyrus fixture writer and a file-lock harness, each in its own file.
They were introduced in #486 PR 1 for the #486 PR 2 migration of 200 assertion sites to 60 xUnit
facts. Those sites produced 204 compiler complaints because some lines carried two. Fourteen
script-property cases need a `.pex` fixture, and three dialogue cases need a held file. The
fixtures and their checks do not involve `src/housecarl-mcp`, `src/housecarl-core` or
`src/housecarl-generator`.

## Contracts

### `PexWriter` — ported, not referenced

`WritePex` / `Decl` / `AutoObj` / `AutoScalar` were lifted from `src/housecarl-generator/ScriptPropertyCheckProbe.cs`
(modulo accessibility and namespace) while that file still existed; #486 PR 2 deleted it, and the
generator's last copy went with `CheckMergeProbe.cs` when the `check-guard` probe moved to this project's
`CheckMergeFixture`, which uses this writer. They write a byte-valid single-object Skyrim `.pex` carrying a chosen
table of Auto properties: the property record plus its `::Name_var` backing variable, Flags =
`Read|Write|AutoVar`, no handler functions, a non-null DocString, an empty auto-state and the empty `''`
state.

It is a **port rather than a project reference** because #486 PR 2 deleted the probe file that held the
generator's copy. It is now the only copy: the generator's re-derived one lived in `CheckMergeProbe.cs`, its
sole remaining caller, and was deleted with that probe. This one is also the only one that ever modelled a
baked initializer: at Aaron's PR #496 gate the generator's `AutoScalar` lost its `int? initInt` parameter,
since neither of its two call sites passed a value. The
probe's copy carried a stale `Mutagen 0.53.1` comment that was deliberately not carried over — the csproj
pins 0.54.4.

The writer has one branch worth naming: an Auto scalar may carry a baked initializer on its backing
variable. The product reads that (`ScriptPropertyCheck`: a scalar with an initializer is not reported
unbound), so the branch is what makes the fixture's `MyDefaulted` mean anything.

That branch writes `VariableType.Integer` while stamping the backing variable's `TypeName` from the caller's
declared type, so it is only honest for an `Int`. `AutoScalar("MyFlag", "Bool", 1)` would produce
`TypeName = "Bool"` over `VariableType = Integer` — a pairing no Papyrus compiler emits, which the product
would then read off `VariableType` and render as `Bool Property MyFlag = 1 Auto`. An arm built on that
fixture would assert product behaviour against a `.pex` shape the game never produces and stay green while
claiming to model a defaulted Bool. So the writer **refuses** a non-`Int` declared type with an initializer,
`ArgumentException` naming the property, the type and the value: the parameter name `initInt` was the only
thing carrying the restriction, and #486 PR 2 spells 14 script-property arms against this machinery.

### `ScriptsWorld` — the probe's records, re-homed as an MO2 instance

The five records the probe plants are carried over with the same EditorIDs, VMAD shapes and property
bindings: a footgun weapon (binds one non-null form, one null form, one quest alias; leaves three
properties unbound), a fully-bound control, a weapon whose script has no compiled `.pex`, a script-free
weapon, and a quest whose script hangs off an *alias* rather than the quest itself. Plus the loose
`HcSpBase.pex` / `HcSpChild.pex` pair that declares what those records do or do not bind.

**What is not the probe's is the world around them.** The probe built a bare temp directory and called
`LoadOrderResolver.Build` + `AssetResolver.Build` directly, which the shipped tool surface cannot be
pointed at. `ScriptsWorld` is a real MO2 instance in `RecordsWorld`'s shape (`ModOrganizer.ini`, a
`profiles/Default/` triple, one mod folder holding both the plugin and its `Scripts\` folder) behind
`LoadOrderService.WithInstance`. That is what lets the same fixture be driven by the service *and* by
`housecarl_check` off the built server.

It does **not** repoint `CorpusRulebook.CorpusPath`. `RecordsWorld` must, and pays for the restore-on-dispose
care that costs; the script-property sweep reads VMADs and `.pex` tables and never the record rulebook, so
this world generates no corpus and touches no process-global.

It cannot be handed to the core `ScriptPropertyCheck.Run(resolver, assets, …)` directly: `LoadOrderService`'s
`Resolver` and `Assets` are private and the world does not re-derive them. Nothing needs that seam (`ValidateScripts` carries every knob the core sweep takes) so it is not opened speculatively.

**The world is frozen**, in the sense `BulkRecordsWorld` states: tests take fixture-known totals from it, so
a later need gets its own world rather than an edit to this one. A test that *mutates* (holding a plugin
open is a mutation by another name, since a held file is unreadable by anything else in the process) builds its own instance instead, or the world's readability would depend on test scheduling. That is why
`HeldOpenTests` builds its own one-plugin world rather than locking this one's `PluginPath`.

#### The `HcSpNoPex` collision, and what the assertions rest on

The no-`.pex` record's EditorID is `HcSpNoPex`, which is also the name of the script it attaches. Renaming
would break the same-EditorIDs fidelity PR 1 is scoped to, so the fixture keeps the collision and the
**assertions are anchored around it**: a bare `Assert.Contains("HcSpNoPex", …)` is satisfied by the record
header alone, so the wire arm asserts the composed reason line (`'Scripts\HcSpNoPex.pex' is not on disk`)
instead, which no record header can produce. If PR 2 finds the collision costs its arms more than it saves,
renaming there is a fixture change with its own review.

#### Why the arms assert what they assert

`ScriptCheckResult.RecordsWithScripts` is incremented *before* any `.pex` is opened, so a world whose
planted `.pex` files were never found still reports four script-bearing records with every declaration
silently unverifiable. Counting cannot tell a resolved fixture from a missing one; the declarations have to
be observed. The loose-layer arm does that — the child's own declaration proves the mod's `Scripts\` folder
is on the asset path at all, and the ancestor's proves the extends chain was walked to a second file.

Two of the fixture's properties exist to produce **no** finding: `MyDefaulted` (the baked initializer) and
`MyAliasBound` (an alias binding, which must not read as bound-but-null). Only a whole-set comparison can
assert an absence, so both finding sets are pinned whole rather than sampled with a predicate.

Text assertions over the rendered document pin a **whole composed line spelled from fixture-known values**,
never a fragment: the phrase "declared but NOT bound" is emitted by both the object and the scalar render
branch, so a fragment of it is satisfied by the wrong finding. Where a structured result carries the same
fact, the service arm asserts that and the wire arm keeps one text line for reachability.

A whole line is still not a whole anchor. Two records in this world carry the object-branch line for
`MySpell`: the footgun, and the alias quest, whose alias script is the same `HcSpChild` and binds nothing —
and because `MySpell`'s declaring script equals its attached script, `ComposeScriptRecordUnit` omits the
`[declared in …]` clause for both, so the two lines are byte-identical. The wire arm therefore asserts the
footgun's `[UNBOUND]` record header **immediately followed by** that line, as one composed span: the header
carries the FormKey, the EditorID and the plugin, which no other record can produce. Measured: with the
footgun's `VirtualMachineAdapter` dropped from the fixture the line-only assertion stayed green off the
quest; the anchored one goes red.

One wire-path smoke test drives `housecarl_set_mo2_instance` then `housecarl_check findings=["scripts"]`
off the built server, to prove the fixture is reachable through the live surface PR 2's arms will use. It
spins its **own** server process: the shared `ServerFixture` is deliberately unconfigured and every stdio
test in the run reads "the body ran" off its config prompt, so configuring it would retune all of them.

#### The scripts family was briefly in both harnesses; the overlap is closed

For the length of PR 1, `ScriptsWorldTests` drove `ValidateScripts` and `housecarl_check
findings=["scripts"]` while `ScriptPropertyCheckProbe.cs` still guarded the same family in the old probe harness; one
probe arm (PEX-ROUNDTRIP) was re-homed here.

**#486 PR 2 closed it**, in the commit that landed the epoch and scripts family facts and retired two
probes: `ScriptPropertyCheckProbe.cs` is deleted and `ScriptsFamilyTests.cs` carries the
`Converted-from: ScriptPropertyCheckProbe` marker. The scripts family now lives in one harness.

### `HeldOpen` — the file-lock harness

An `IDisposable` that holds one file with `FileShare.None` and releases on dispose: MO2 or xEdit sitting on
a plugin, which is the scenario houseCARL's no-handles-at-rest design explicitly invites. The mechanism is
ported from `DialogueInfoOrderProbe.cs`'s `UNREAD-WIRED` / `DEFINER-LOCK-LOUD` / `WINNER-LOCK-LOUD` arms.

The one thing it adds over an inline `FileStream` is that **acquisition failure throws**. Each of those
probe arms wraps its own open in a try/catch whose catch marks the arm FAILED, because an arm that could
not take the lock has not driven the path it names and would otherwise assert nothing while still reporting
a pass. `Hold` makes that branch unreachable by omission.

Nothing else was ported. The arms' own assertions are on product results (`CheckError`, the rendered
sentence), which are PR 2's subject.

The proofs use `LoadOrderResolver.OpenOverlay` as "an engine read" (the engine's documented single
overlay-open choke point) which keeps the harness's proofs about the harness rather than pre-empting PR 2.
A binary overlay is memory-mapped, so an undisposed one keeps a handle on the plugin for the rest of the
process; the arms dispose it the way every product call site does, and each arm's last line calls
`AssertNoHandlesLeft()` — a loud `Directory.Delete`, which Windows refuses while any handle is open. That
runs at the end of the body rather than from `Dispose`, because a throwing `Dispose` replaces a real
assertion failure with its own. On Linux, deleting an open file is permitted, so that cleanup is not
proof of zero open handles.

The retained recovery test asserts `IOException` while the hold is active, then releases it and reads
the exact expected weapon EditorID. This covers both loud read failure and recovery without requiring
a Windows error number. The separate control that also pinned `ERROR_SHARING_VIOLATION` was retired
in the [Linux / Amethyst coverage audit](../testing/linux-amethyst-test-audit-2026-10-14/index.html);
its full source and assertion mapping remain in the removal ledger.

#### `Read<T>` — `use` must materialise everything it returns

The arms' `Read<T>` helper unmaps the overlay in a `finally`, so the overlay is gone *before* the value
crosses the return. Every value-returning call site must therefore materialise inside `use` (a string, a
count, a copy) and the helper refuses a `T` that is an `IModGetter` or an `IMajorRecordGetter` before it
opens the plugin at all. That refusal exists because the failure is otherwise silent: measured on this
fixture, `Read(path, mod => Assert.Single(mod.Weapons))` returned a record whose `EditorID` then read back
correctly off the unmapped view and the arm reported a pass. The documented outcomes are an
`ObjectDisposedException` at best and an `AccessViolationException` that takes the runner down at worst, and
which one a caller gets is not the caller's to choose — so the hazard is refused at the type rather than
left to the fixture's size. The check is a static one on `typeof(T)`: a caller who erases the type (returning
`object`) walks past it, which is the limit of what a cheap guard buys. Same contract the product states on
`OverlaySession` ("the service reads fields off a fetched body before its session disposes"); this is that
sentence, restated where PR 2's three dialogue lock arms will read it.

## Pinned by

Linux wire regressions reuse the original synthetic records and loose assets through
`AmethystFixtureConnection`, which saves manifests and profile intent with Amethyst's native producer.
The helper is for loose-file worlds; archive membership is covered by the existing archive fixture.
Snapshot tests check explicit stale/busy refusal, manager refresh, pinned roots and Papyrus maps.
They do not recreate MO2's serve-old-assets fallback. Permission-denial fixtures verify the failure
really occurs and restore directory modes during cleanup.

- *`PexWriter` (ported, not referenced*: both branches are pinned) `ScriptsWorldTests.TheWriterRefusesABakedInitializerOnANonIntScalar` and
  `TheWriterStillBakesAnIntScalarInitializerAndItRoundTrips`.
- *`ScriptsWorld` (the probe's records, re-homed as an MO2 instance*:
  `ScriptsWorldTests.TheServiceSweepsTheInstanceAndExactlyTheVmadCarryingRecordsAreScriptBearing`) the four
  script-bearing records, with the script-free weapon not counted.
- *Why the arms assert what they assert*: `ScriptsWorldTests.BothPlantedPexFilesResolveThroughTheLooseLayer_SoTheDeclarationsAreRealNotUnverifiable`
  — the declarations are observed through the loose layer, and both finding sets are pinned whole;
  `CheckOverTheWireReportsTheFixtureKnownScriptCountAndNamesTheUnverifiableScript` — the wire-path smoke test on
  its own server.
- *`HeldOpen` (the file-lock harness*: `HeldOpenTests.AfterTheHoldIsDisposedTheSameReadSucceeds`) the held read throws, then release restores the expected record; `HoldingAPathThatDoesNotExistThrowsAndNamesThePath`
  — acquisition failure throws (all in the same class).
- *`Read<T>` (`use` must materialise everything it returns*: `HeldOpenTests.ReadRefusesAValueThatWouldStillBeAViewOnTheUnmappedOverlay`) a `T` that is still a view on the overlay is refused before the plugin is opened.

## Where

`src/housecarl-mcp-tests/PexWriter.cs` writes the `.pex` fixtures (`WritePex`, `Decl`, `AutoObj`, `AutoScalar`);
`src/housecarl-mcp-tests/ScriptsWorld.cs` is the Papyrus world behind `LoadOrderService.WithInstance`;
`src/housecarl-mcp-tests/HeldOpen.cs` is the file-lock harness (`Hold`). Their own tests are `ScriptsWorldTests.cs` and
`HeldOpenTests.cs` in the same project. No tool of its own; the wire-path smoke test drives
`housecarl_set_mo2_instance` and `housecarl_check`.
