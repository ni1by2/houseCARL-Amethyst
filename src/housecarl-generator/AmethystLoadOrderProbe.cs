using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>Locks Amethyst's priority, activation, locking, casing, and source rules.</summary>
public static class AmethystLoadOrderProbe
{
    public static int RunGuard(string[] args)
    {
        var root = Path.Combine(Path.GetTempPath(), "hc-amethyst-order-" + Guid.NewGuid().ToString("N"));
        try
        {
            var profile = Path.Combine(root, "profiles", "default");
            var mods = Path.Combine(root, "mods");
            var overwrite = Path.Combine(root, "overwrite");
            var data = Path.Combine(root, "Skyrim", "Data_Core");
            Directory.CreateDirectory(profile);

            Write(Path.Combine(profile, "modlist.txt"),
                "+high mod\n*Locked Ω\n-Disabled\n-Tools_separator\n+Low\n");
            Write(Path.Combine(profile, "plugins.txt"),
                "*Skyrim.esm\n*Duplicate.esp\nInactive.esp\n*Locked.esl\n*Overwrite.esp\n*Missing.esp\n");
            Write(Path.Combine(profile, "loadorder.txt"),
                "Skyrim.esm\nInactive.esp\nDuplicate.esp\nLocked.esl\nOverwrite.esp\nImplicit.esm\nMissing.esp\n");

            Touch(Path.Combine(mods, "High Mod", "Duplicate.ESP"));
            Touch(Path.Combine(mods, "Locked Ω", "Locked.esl"));
            Touch(Path.Combine(mods, "Disabled", "Inactive.esp"));
            Touch(Path.Combine(mods, "Low", "Duplicate.esp"));
            Touch(Path.Combine(overwrite, "Overwrite.esp"));
            Touch(Path.Combine(data, "Skyrim.esm"));
            Touch(Path.Combine(data, "Implicit.esm"));

            var composition = AmethystLoadOrder.ReadComposition(profile);
            Equal(new[] { "high mod", "Locked Ω", "Low" }, composition.EnabledMods, "enabled priority");
            Equal(new[] { "Locked Ω" }, composition.LockedMods, "locked mods");
            Equal(new[] { "Disabled" }, composition.DisabledMods, "disabled mods");

            var result = AmethystLoadOrder.Build(profile, mods, data, overwrite);
            Equal(6, result.ActiveCount, "active count");
            Equal(5, result.ResolvedCount, "resolved count");
            EndsWith(result.ResolvedSources["Duplicate.esp"], "High Mod/Duplicate.ESP", "highest-priority duplicate");
            EndsWith(result.ResolvedSources["Overwrite.esp"], "overwrite/Overwrite.esp", "overwrite source");
            EndsWith(result.ResolvedSources["Implicit.esm"], "Data_Core/Implicit.esm", "implicit vanilla source");
            True(result.Warnings.Count == 1 && result.Warnings[0].Contains("Missing.esp", StringComparison.Ordinal),
                "missing plugin warning");

            Console.WriteLine("PASS: Amethyst load-order semantics and native plugin sources");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL: " + ex);
            return 1;
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    static void Write(string path, string value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, value);
    }

    static void Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Array.Empty<byte>());
    }

    static void Equal<T>(T expected, T actual, string arm) where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{arm}: expected '{expected}', got '{actual}'");
    }

    static void Equal(IReadOnlyList<string> expected, IReadOnlyList<string> actual, string arm)
    {
        if (!expected.SequenceEqual(actual, StringComparer.Ordinal))
            throw new InvalidOperationException($"{arm}: expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}]");
    }

    static void EndsWith(string actual, string suffix, string arm)
    {
        if (!actual.Replace('\\', '/').EndsWith(suffix, StringComparison.Ordinal))
            throw new InvalidOperationException($"{arm}: '{actual}' does not end with '{suffix}'");
    }

    static void True(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
