# Filesystem path model

houseCARL-Amethyst uses two path domains. Treating them as interchangeable is a
correctness and security bug on Linux.

## Bethesda paths

A Bethesda path identifies a file relative to Skyrim's `Data` directory or an
entry inside a BSA. Its separator is always `\`, and comparison is
case-insensitive. It is retained in plugin fields, archive lookups, diagnostics,
MCP responses, and Skyrim-format output.

Examples:

```text
Meshes\Actors\Character\FaceGenData\FaceGeom\Skyrim.esm\00013BBF.nif
Sound\Voice\Example.esp\FemaleNord\line_00001234_1.fuz
```

User-supplied Data paths must be relative and non-empty. The path gate rejects:

- native or Bethesda root prefixes;
- Windows drive prefixes;
- NUL characters;
- empty, `.` or `..` segments.

These rules prevent a read or write from escaping the selected Data root and
avoid platform-dependent normalization.

## Host paths

A host path is an absolute or relative Linux filesystem path. It uses native
separators and the exact casing stored by the filesystem. Only host paths may be
passed to `File`, `Directory`, or `Path` filesystem operations.

`BethesdaPath` is the boundary between the domains:

- `Normalize` validates and canonicalizes untrusted Data-relative input.
- `Under` converts a Bethesda path to a native path beneath a known root.
- `TryResolveExisting` walks each segment case-insensitively and returns the raw
  on-disk casing.
- `FromHostRelative` converts enumeration results back to canonical form.
- `NormalizeArchiveEntry` is deliberately lenient for trusted BSA table input.

## Why case-preserving lookup is required

Skyrim and BSA lookup are case-insensitive; common Linux filesystems are not.
For example, the query `meshes\actors\head.nif` must find an on-disk path named
`Meshes/Actors/Head.NIF`. Converting separators without resolving the real casing
would produce a valid-looking path that does not exist.

Amethyst's `filemap.txt` and `modindex.bin` will become the authoritative winner
and raw-source-casing index in Session 4. The same boundary remains in force:
logical keys stay Bethesda paths, while indexed source paths are converted to
host paths only at the final I/O edge.
