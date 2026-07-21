using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace HousecarlCore;

/// <summary>Linux device/inode identity used to distinguish hardlinks from copies.</summary>
public sealed record LinuxFileIdentity(ulong Device, ulong Inode)
{
    const int AtFdcwd = -100;
    const uint StatxBasicStats = 0x07ff;

    public static LinuxFileIdentity? Read(string path)
    {
        if (!OperatingSystem.IsLinux() || !File.Exists(path)) return null;
        return statx(AtFdcwd, path, 0, StatxBasicStats, out var value) == 0
            ? new(((ulong)value.DeviceMajor << 32) | value.DeviceMinor, value.Inode)
            : null;
    }

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    static extern int statx(int dirfd, string path, int flags, uint mask, out Statx value);

    [StructLayout(LayoutKind.Sequential)]
    struct Timestamp { public long Seconds; public uint Nanoseconds; public int Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    struct Statx
    {
        public uint Mask, BlockSize;
        public ulong Attributes;
        public uint LinkCount, UserId, GroupId;
        public ushort Mode, Spare0;
        public ulong Inode, Size, Blocks, AttributesMask;
        public Timestamp Access, Birth, Change, Modify;
        public uint RdevMajor, RdevMinor, DeviceMajor, DeviceMinor;
        public ulong Spare1, Spare2, Spare3, Spare4, Spare5, Spare6, Spare7;
        public ulong Spare8, Spare9, Spare10, Spare11, Spare12, Spare13, Spare14;
    }
}

/// <summary>One staged write awaiting a later Amethyst filemap rebuild and deployment.</summary>
public sealed class PendingAmethystWrite
{
    public required string ProfileName { get; set; }
    public required string StagingPath { get; set; }
    public required string DataRelativePath { get; set; }
    public required string ContentSha256 { get; set; }
    public required DateTime WrittenUtc { get; set; }
    public required string Kind { get; set; }
    public LinuxFileIdentity? StagingIdentity { get; set; }
    public LinuxFileIdentity? PreviousStagingIdentity { get; set; }
    public bool? DeployedWasSameHardlink { get; set; }
}

/// <summary>Pure helpers for recording and verifying staged deployment state.</summary>
public static class AmethystRedeploy
{
    public static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    public static bool IsVerified(PendingAmethystWrite pending, ManagerSnapshot snapshot)
    {
        if (!snapshot.DeploymentActive
            || !string.Equals(pending.ProfileName, snapshot.ActiveProfileName, StringComparison.Ordinal)
            || !Newer(snapshot, snapshot.FilemapPath, pending.WrittenUtc)
            || !Newer(snapshot, snapshot.DeployStateFile, pending.WrittenUtc)
            || !snapshot.LooseAssetSources.TryGetValue(pending.DataRelativePath, out var source)
            || !SamePath(source.HostPath, pending.StagingPath)
            || !File.Exists(pending.StagingPath)) return false;

        var deployed = BethesdaPath.Under(Path.Combine(snapshot.GamePath, "Data"), pending.DataRelativePath);
        if (!File.Exists(deployed)) return false;
        try { if (Hash(pending.StagingPath) != pending.ContentSha256) return false; }
        catch { return false; }
        var stagingIdentity = LinuxFileIdentity.Read(pending.StagingPath);
        var deployedIdentity = LinuxFileIdentity.Read(deployed);
        if (stagingIdentity is not null && stagingIdentity == deployedIdentity) return true;
        try { return pending.ContentSha256 == Hash(deployed); }
        catch { return false; }
    }

    static bool Newer(ManagerSnapshot snapshot, string path, DateTime written) =>
        snapshot.FreshnessInputs.TryGetValue(path, out var stamp) && stamp > written;

    static bool SamePath(string left, string right)
    {
        try { return Path.GetFullPath(left) == Path.GetFullPath(right); }
        catch { return false; }
    }
}
