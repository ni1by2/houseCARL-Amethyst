namespace HousecarlCore;

/// <summary>Refuses Windows executables on Linux when callers have not supplied a resolved runner.</summary>
public static class WindowsProgramGuard
{
    public static string? Refusal(string path, string program)
    {
        if (!OperatingSystem.IsLinux()) return null;
        // Callers use this only for Windows tools (PapyrusCompiler.exe and BSArch.exe). Refuse by
        // platform and role, rather than treating a non-PE file at an .exe path as runnable code.
        return $"{program} is a Windows executable; no resolved Proton runner was supplied. Linux execution requires the selected Amethyst prefix plus Steam's current CompatToolMapping and installed tool identity; prefix config_info may be stale. Resolve the runner from the active saved Amethyst snapshot before launching CK/BSArch.";
    }
}
