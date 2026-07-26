using System.ComponentModel;
using ModelContextProtocol.Server;

namespace HousecarlMcp;

/// <summary>Reserves the Papyrus compilation tool name for the post-v1 Proton runner.</summary>
[McpServerToolType]
public static class CompileTools
{
    /// <summary>Refuses compilation until a structured external-command contract exists.</summary>
    [McpServerTool(Name = "housecarl_compile_script", Title = "Compile a Papyrus script (.psc → .pex)"),
     Description(
         "Reserved post-v1 surface for Papyrus compilation. Execution is unavailable until houseCARL-Amethyst has a " +
         "structured Proton command contract that preserves arguments, environment, prefix, and working directory. " +
         "Record editing, PEX decompilation, NIF operations, archive reads, and Amethyst staging remain native.")]
    public static string CompileScript(
        [Description("Full native path to the .psc source file.")]
            string script,
        [Description("Reserved for future semicolon-separated native import directories.")]
            string? import_dirs = null,
        [Description("Reserved future output-mod name.")]
            string? patch_name = null,
        [Description("Reserved future existing houseCARL staging-mod target.")]
            string? into = null,
        [Description("Reserved future native output-mod root.")]
            string? output_dir = null) =>
        "error: Papyrus compilation is deferred until houseCARL-Amethyst implements the planned structured Proton runner. " +
        "Native record, archive-read, PEX-decompile, NIF, and Amethyst staging features remain available.";
}
