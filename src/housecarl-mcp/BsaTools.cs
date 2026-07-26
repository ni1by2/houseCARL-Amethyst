using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;

namespace HousecarlMcp;

/// <summary>
/// Lists and extracts Bethesda archives natively through Mutagen. Repacking remains a separate BSArch-backed
/// operation until the post-v1 structured Proton runner is implemented. Managed outputs go to Amethyst staging;
/// originals are not modified.
/// </summary>
[McpServerToolType]
public static class BsaTools
{
    /// <summary>Lists the canonical entry paths stored in one Bethesda archive.</summary>
    [McpServerTool(Name = "housecarl_bsa_list", ReadOnly = true, Title = "List a .bsa archive's contents"),
     Description(
         "List the files inside a Bethesda .bsa archive. Returns the archive format + the contained file paths. " +
         "Read-only — extracts nothing. Reads the archive directly (via Mutagen) — no external tool needed. To read a " +
         "file's CONTENTS, use housecarl_bsa_extract then read the file.")]
    public static string BsaList(
        [Description("Full path to the .bsa archive to list.")]
            string archive,
        [Description("Optional. Max characters before the file list is cut with an explicit notice. 0 = the server default (~80k).")]
            int max_chars = 0) => Guard.Tool("housecarl_bsa_list", () =>
    {
        if (string.IsNullOrWhiteSpace(archive)) return "error: no archive given. Pass the full path to the .bsa.";
        try { archive = Path.GetFullPath(archive.Trim().Trim('"')); }
        catch (Exception ex) { return $"error: '{archive}' is not a usable path ({ex.Message})."; }
        if (!File.Exists(archive)) return $"error: no such file: '{archive}'.";

        var r = HousecarlCore.BsaArchive.List(archive);
        if (!r.Ran) return "error: " + r.RunError;
        if (!r.Success) return "error: " + r.Raw;   // header-vs-reader file-count mismatch (possible corruption)

        int cap = max_chars > 0 ? max_chars : 80_000;
        var sb = new StringBuilder();
        sb.Append(Path.GetFileName(archive)).Append("  [").Append(r.Format ?? "unknown format").Append("]  ")
          .Append(r.DeclaredCount).Append(" file(s)\n");
        int shown = 0;
        foreach (var f in r.Files)
        {
            if (sb.Length >= cap) { sb.Append("  ... [").Append(r.Files.Count - shown).Append(" more omitted at max_chars=").Append(cap).Append("]\n"); break; }
            sb.Append("  ").Append(f).Append('\n'); shown++;
        }
        return sb.ToString().TrimEnd('\n');
    });

    /// <summary>Extracts an entire archive into a requested folder or new Amethyst staging mod.</summary>
    [McpServerTool(Name = "housecarl_bsa_extract", Title = "Extract a .bsa archive to a folder"),
     Description(
         "Extract a Bethesda .bsa archive's contents to a folder so you can read the files. Reads the archive directly " +
         "(via Mutagen — handles compressed archives too) — no external tool needed. Unpacks the WHOLE archive. Pass " +
         "dest= a folder to unpack into; OMIT dest to let houseCARL unpack into a NEW reviewable mod folder under your " +
         "mods directory (reported back) — that requires an Amethyst connection. Originals are never modified.")]
    public static string BsaExtract(
        LoadOrderService svc,
        [Description("Full path to the .bsa archive to extract.")]
            string archive,
        [Description("Optional. Folder to unpack into. If omitted, houseCARL creates a NEW mod folder under your mods directory and reports its path.")]
            string? dest = null) => Guard.Tool("housecarl_bsa_extract", () =>
    {
        if (string.IsNullOrWhiteSpace(archive)) return "error: no archive given. Pass the full path to the .bsa.";
        try { archive = Path.GetFullPath(archive.Trim().Trim('"')); }
        catch (Exception ex) { return $"error: '{archive}' is not a usable path ({ex.Message})."; }
        if (!File.Exists(archive)) return $"error: no such file: '{archive}'.";

        string target;
        bool managed = string.IsNullOrWhiteSpace(dest);
        if (managed)
        {
            if (svc.ConfigPromptOrNull() is { } cfg) return cfg;   // need ModsDir for the default managed folder
            // Extract keeps its own #49 residue contract (the "left at X" message below) — out of H2's delete-if-empty
            // scope, which Aaron set to repack/compile/decompile. Just take the output dir.
            try { target = svc.ResolvePatchModFolder(Path.GetFileNameWithoutExtension(archive) + " (extracted)", into: null, "houseCARL_Extract").OutputDir; }
            catch (InvalidOperationException ex) { return "error: " + ex.Message; }
        }
        else
        {
            target = Path.GetFullPath(dest!.Trim().Trim('"'));
        }

        string residue = managed ? $"\nThe freshly created mod folder was left at '{target}' — delete it or retry into it." : "";
        var r = HousecarlCore.BsaArchive.Unpack(archive, target);
        if (!r.Ran) return "error: " + r.RunError + residue;   // archive couldn't be opened/read
        if (!r.Success)                                          // path-traversal refusal or a mid-extract error (Q3)
            return "extract FAILED: " + r.Raw + residue;

        var sb = new StringBuilder();
        sb.Append("extracted ").Append(Path.GetFileName(archive)).Append(" → ").Append(target).Append('\n');
        sb.Append(r.Raw).Append('\n');   // e.g. "extracted 5826 file(s)."
        sb.Append(managed
            ? "(a new Amethyst staging mod — read the files you need from it; enable and deploy it only if you want the loose files in your load order.)"
            : "(read the files you need from that folder.)");
        return sb.ToString();
    });

    /// <summary>Refuses repacking until the structured Proton command runner is implemented.</summary>
    [McpServerTool(Name = "housecarl_bsa_repack", Title = "Pack a folder into a .bsa archive"),
     Description(
         "Reserved post-v1 surface for packing loose files into a Bethesda archive. Execution is unavailable until " +
         "houseCARL-Amethyst has a structured Proton command contract for BSArch. Native archive listing and extraction " +
         "remain available without Proton.")]
    public static string BsaRepack(
        [Description("Full path to the source folder of loose files to pack (its tree becomes the archive's contents).")]
            string source_folder,
        [Description("Optional. The .bsa filename to create (default: the source folder's name + '.bsa').")]
            string? archive_name = null,
        [Description("Optional. Archive format: 'sse' (default, Skyrim SE), 'tes5' (Skyrim LE), 'fo4', 'fo4dds', 'sf1', 'sf1dds', 'tes4', 'fo3', 'fnv', 'tes3'.")]
            string? format = null,
        [Description("Optional. Compress the archive (default false). WARNING: compression breaks sounds/voices — leave false if the folder contains any audio.")]
            bool compress = false,
        [Description("Optional. Base name for the NEW mod folder the .bsa lands in (default 'houseCARL_Archive'); auto-suffixed if taken.")]
            string? patch_name = null,
        [Description("Optional. Filename of an existing houseCARL patch mod to place the .bsa into instead of a fresh folder. Found by plugin filename even if its Amethyst mod folder was renamed; for duplicate filenames, pass the mod-folder name.")]
            string? into = null) =>
        "error: BSA repacking is deferred until houseCARL-Amethyst implements the planned structured Proton runner. " +
        "Use housecarl_bsa_list and housecarl_bsa_extract for native read operations.";
}
