# houseCARL code documentation standard

**Class:** LIVING standard. Update it when the code-review needs change.

This standard makes the complete inherited and fork-authored C# codebase readable
without requiring the reader to reconstruct intent from syntax or git history.
Concise implementation remains a goal, but brevity must not hide contracts, safety
rules, external formats, or the reason behind a non-obvious choice.

## Required documentation

Every namespace-level type, nested type, delegate, constructor, property, field,
and function receives plain-English XML documentation. This includes private
helpers and test/probe code, not only the public API.

Function documentation states, as applicable:

- what responsibility the function owns;
- what each parameter means, including units, path domain, null meaning, and valid
  states that the type system does not express;
- what the result means, especially false, null, empty, or partial results;
- observable side effects such as filesystem writes, cache changes, persistence,
  logging, or process execution;
- expected exceptions and fail-closed behavior;
- important preconditions, postconditions, and safety invariants.

Properties and fields explain domain meaning rather than restating their names.
Record positional parameters are documented on the record when their meaning is
not completely represented by their types.

## Inline explanations

Use nearby ordinary comments where the reader needs to understand *why* a block
exists or why the obvious-looking alternative is unsafe. Required examples include:

- filesystem and deployment safety boundaries;
- Bethesda-path versus Linux-host-path conversion;
- atomic-write and freshness ordering;
- reflection assumptions that preserve full Mutagen coverage;
- external binary formats, native ABI layouts, and compatibility shims;
- fail-loud branches and deliberately swallowed cleanup failures;
- dense transformations whose intermediate states are not apparent.

Do not narrate syntax line by line. Comments such as "increment the counter" or
"return the result" add noise and make the actual contract harder to find. A short
function should still have an XML contract, but its self-evident statements need no
paraphrase.

## Probe and test documentation

Each probe entry point explains the risk it guards. Scenario helpers identify the
starting state, action, and invariant proved. Assertions remain specific enough that
a failure explains the violated contract without consulting the implementation.

## Review procedure

The full pass covers all code under `src/`, including code inherited unchanged from
upstream houseCARL. Review proceeds component by component so each commit remains
readable:

1. Fork-specific Amethyst layout, filemap, path, and redeployment code.
2. Shared core record, asset, archive, Papyrus, NIF, and write engines.
3. MCP service and tool surface.
4. Setup/packaging code.
5. Generator, proof harnesses, and all probes.
6. Final cross-component terminology and stale-comment audit.

A file is complete only after every declaration has been inspected, not merely
because a compiler warning is absent. Generated data and third-party source are out
of scope; code that generates them is in scope.

## Completion gate

The documentation milestone is complete only when:

- every in-scope declaration has the required XML contract;
- every non-obvious invariant has a local plain-English explanation;
- all XML references and the solution build succeed;
- documentation describes current Amethyst/Linux behavior and does not preserve
  stale MO2/Windows claims except in explicitly marked legacy compatibility code;
- the roadmap records the components reviewed, verification performed, and any
  deliberately deferred ambiguity.

