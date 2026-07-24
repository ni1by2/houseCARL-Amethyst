namespace HousecarlCore;

/// <summary>The ONE embedded-JSON resource reader — the SkyPatcher catalog and field map ship as
/// housecarl-core embedded resources and each hand-rolled an identical loader (deduped, PR #165
/// review cleanup). Throws loudly on a missing resource: a silently-empty load is exactly the Q3
/// degrade both call sites' Load() contracts forbid.</summary>
internal static class EmbeddedJson
{
    /// <summary>Reads one uniquely suffix-matched resource from the housecarl-core assembly as UTF-8 text.</summary>
    /// <param name="fileName">Resource filename suffix, compared ordinally without case.</param>
    /// <param name="what">Human-readable artifact name included in a missing-resource diagnostic.</param>
    /// <returns>The complete embedded JSON text.</returns>
    /// <exception cref="InvalidOperationException">No embedded resource ends with <paramref name="fileName"/>.</exception>
    public static string Read(string fileName, string what)
    {
        var asm = typeof(EmbeddedJson).Assembly;
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"{what} resource '{fileName}' is not embedded in housecarl-core.");
        using var s = asm.GetManifestResourceStream(name)!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
