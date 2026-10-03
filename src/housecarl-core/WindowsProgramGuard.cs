namespace HousecarlCore;

/// <summary>Refuses Windows executables on Linux until the Proton execution contract is implemented.</summary>
public static class WindowsProgramGuard
{
    public static string? Refusal(string path, string program)
    {
        if (!OperatingSystem.IsLinux()) return null;
        try
        {
            using var file = File.OpenRead(path);
            if (file.ReadByte() == 'M' && file.ReadByte() == 'Z')
                return $"{program} is a Windows executable; running it through Proton on Linux is not supported yet.";
            return null;
        }
        catch (Exception ex)
        {
            return $"could not inspect {program} at '{path}' before execution ({ex.GetType().Name}: {ex.Message}).";
        }
    }
}
