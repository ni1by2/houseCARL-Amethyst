namespace HousecarlGenerator;

/// <summary>
/// Supplies optional real-game inputs to exploratory probes without embedding a developer's filesystem layout.
/// Synthetic CI probes do not use these values.
/// </summary>
internal static class ProbeInputs
{
    /// <summary>The native Skyrim Data directory supplied for an explicit local probe run.</summary>
    internal static string DataDirectory =>
        Environment.GetEnvironmentVariable("HOUSECARL_PROBE_DATA_DIR") ?? "";

    /// <summary>The native Amethyst staging directory supplied for an explicit local probe run.</summary>
    internal static string ModsDirectory =>
        Environment.GetEnvironmentVariable("HOUSECARL_PROBE_MODS_DIR") ?? "";

    /// <summary>An optional Papyrus compiler executable, normally invoked through Proton on Linux.</summary>
    internal static string PapyrusCompiler =>
        Environment.GetEnvironmentVariable("HOUSECARL_PAPYRUS_COMPILER") ?? "";

    /// <summary>An optional real NIF used only by existence-gated smoke-test arms.</summary>
    internal static string NifSmoke =>
        Environment.GetEnvironmentVariable("HOUSECARL_NIF_SMOKE") ?? "";

    /// <summary>
    /// Resolves a file under the configured Data directory, or returns an empty path when no directory was supplied.
    /// Returning empty keeps callers' existing fail-loud or clean-skip behavior without probing the current directory.
    /// </summary>
    internal static string DataFile(string fileName) =>
        string.IsNullOrWhiteSpace(DataDirectory) ? "" : Path.Combine(DataDirectory, fileName);
}
