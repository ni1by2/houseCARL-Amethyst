# Verified producer and native reader mappings

Version 2.1.4 accepts API 13, schema 10, engine 1, rules 18 from Amethyst 2.6.3 at `788a71262ca479b6cbccc53ff3a0083250e3833c` using the existing rules-15 native reader pinned to `7de35eaf0c7191defa7aee33d61452ddf73f7da3`. Rules 19 from 2.6.4-beta.1 uses a fourth reader pinned to its producer at `8ac86bdb9bc6f32b5c49eae69890ae96e4f16943`. Earlier houseCARL-Amethyst 2.1.3 packages do not contain these updates.

The producer contract identifies the saved catalog. The native reader's revision identifies the code opening a private backup. Those stamps need not be equal when a particular pairing has been verified. The runtime keeps an explicit mapping between them and checks the actual reader against that mapping. It still requires API 13, the captured schema, engine 1, readiness, inventory generation and profile generation.

The captured producer remains `amethyst-schema10-engine1-rules18` or `amethyst-schema10-engine1-rules19` in transaction journals and freshness checks. A catalog changing to another supported revision during a call still refuses. Supplying a module other than the mapped reader also refuses. Schema 11, engine 2 and unverified rules revisions remain unsupported; this change grants no open-ended forward compatibility.

## Why the existing reader can be tested

The native source diff from rules 15 to 18 changes conflict-status accounting and the conflict-only flag at bit 10. It leaves the schema, API, engine revision and deployment-plan revision unchanged. Conflict-only files remain deployable and participate in winner/provider selection. houseCARL consumes saved file identity, native winners and provider chains, and does not expose Amethyst's conflict-status counters as its own results.

The [compatibility suite's recorded matrix](../testing/amethyst-compatibility-suite-2026-11-08.md) compared the rules-14/rules-15 readers with the real rules-18 producer using bounded synthetic winner/provider cases. The separate rules-18 C# fixture extends runtime coverage to root Top Level routing, conflict-only files, original producer identity, source changes and staged-write races. Broader adapter/transaction tests remain required before accepting a new producer.

Rules 19 preserves ordinary mod-key and archive-key casing. The initial bounded comparison passed, but the expanded case with distinct `Beta` and `beta` folders, `Alpha` between them, and all archives bound to the same plugin exposed wrong native winners from readers 14, 15 and 18. Their own rules-19 control passes. The new matching reader is therefore required. C# archive copy ranking also uses exact saved keys, matching Amethyst, while Skyrim plugin names retain their existing case-insensitive handling.

Named reads and provider-filtered queries use the entire captured provider registry for rules 19. Exact spelling wins; a case-insensitive alias remains available only when it identifies one name. `beta` cannot select `Beta`, including when only the lowercased folder has a particular file. An ambiguous alias such as `BETA` refuses with the exact alternatives. Earlier contracts retain their existing name matching.

The genuine producer fixtures check complete provider chains, physical source paths, original contract identity and write races. Their SQL serializer verifies every table after a round trip and preserves embedded NUL separators; SQLite's default SQL dump truncates such TEXT values and cannot supply this archive test faithfully.

The [10 November verification record](../testing/amethyst-runtime-compatibility-2026-11-10.md) records the final regression, package and producer checks.

## Updating compatibility

Run the compatibility experiment for a new upstream tag or commit and inspect source changes and reports. A changed rules stamp alone does not require another native binary. A verified producer/reader pairing can be added to the admission mapping with its fixture and affected runtime tests. New schema or API coverage, changed winner semantics or failing safety checks can require a reader or adapter change.

There is no automatic approval from a green comparison report, and the runtime does not learn new contracts from report files. Existing per-plugin consent, library locking, source pinning, round-trip verification and deployment checks remain in place. This update does not replace the deferred complete-snapshot API refactor, certify gameplay or install anything locally.
