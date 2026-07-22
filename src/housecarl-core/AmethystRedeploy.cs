using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace HousecarlCore;

/// <summary>Identifies one Linux filesystem object independently of its path.</summary>
/// <remarks>
/// Two paths with the same device and inode refer to the same underlying file, which is how the
/// redeployment checks distinguish an Amethyst hardlink from a byte-for-byte copy.
/// </remarks>
/// <param name="Device">Combined Linux major/minor identifier of the containing filesystem.</param>
/// <param name="Inode">Filesystem-local identifier of the underlying file object.</param>
public sealed record LinuxFileIdentity(ulong Device, ulong Inode)
{
    // Linux statx interprets this value as "resolve the supplied path from the current directory."
    const int AtFdcwd = -100;

    // Request every field in statx's basic-stat group, including the inode and device numbers used below.
    const uint StatxBasicStats = 0x07ff;

    /// <summary>Reads the Linux device and inode for an existing file.</summary>
    /// <param name="path">Native host path to inspect.</param>
    /// <returns>
    /// The file identity, or <see langword="null"/> when the host is not Linux, the file does not
    /// exist, or Linux cannot stat it. Callers treat an unavailable identity as "not proven" and
    /// fall back to content verification; they never infer that two files are hardlinked.
    /// </returns>
    public static LinuxFileIdentity? Read(string path)
    {
        if (!OperatingSystem.IsLinux() || !File.Exists(path)) return null;
        return statx(AtFdcwd, path, 0, StatxBasicStats, out var value) == 0
            ? new(((ulong)value.DeviceMajor << 32) | value.DeviceMinor, value.Inode)
            : null;
    }

    /// <summary>Calls Linux <c>statx(2)</c> without passing the path through a shell.</summary>
    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    static extern int statx(int dirfd, string path, int flags, uint mask, out Statx value);

    /// <summary>Native <c>struct statx_timestamp</c> layout required to reach later stat fields safely.</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct Timestamp
    {
        /// <summary>Whole seconds since the Unix epoch.</summary>
        public long Seconds;

        /// <summary>Fractional nanoseconds following <see cref="Seconds"/>.</summary>
        public uint Nanoseconds;

        /// <summary>Kernel-reserved padding that preserves the native ABI layout.</summary>
        public int Reserved;
    }

    /// <summary>
    /// Native Linux <c>struct statx</c> layout. Unused fields remain present because omitting them
    /// would shift the inode and device fields and make the hardlink comparison read invalid memory.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct Statx
    {
        /// <summary>Bit mask stating which requested fields Linux supplied.</summary>
        public uint Mask;

        /// <summary>Preferred I/O block size; retained only to preserve native field offsets.</summary>
        public uint BlockSize;

        /// <summary>Linux file-attribute flags; retained only to preserve native field offsets.</summary>
        public ulong Attributes;

        /// <summary>Number of hardlinks to the file.</summary>
        public uint LinkCount;

        /// <summary>Owning user identifier; retained only to preserve native field offsets.</summary>
        public uint UserId;

        /// <summary>Owning group identifier; retained only to preserve native field offsets.</summary>
        public uint GroupId;

        /// <summary>File type and permission bits; retained only to preserve native field offsets.</summary>
        public ushort Mode;

        /// <summary>Kernel-reserved padding that preserves the native ABI layout.</summary>
        public ushort Spare0;

        /// <summary>Filesystem-local identity used by the hardlink comparison.</summary>
        public ulong Inode;

        /// <summary>File length; retained only to preserve native field offsets.</summary>
        public ulong Size;

        /// <summary>Allocated 512-byte blocks; retained only to preserve native field offsets.</summary>
        public ulong Blocks;

        /// <summary>Mask of supported attribute flags; retained only to preserve native field offsets.</summary>
        public ulong AttributesMask;

        /// <summary>Last-access timestamp; retained only to preserve native field offsets.</summary>
        public Timestamp Access;

        /// <summary>Creation timestamp; retained only to preserve native field offsets.</summary>
        public Timestamp Birth;

        /// <summary>Metadata-change timestamp; retained only to preserve native field offsets.</summary>
        public Timestamp Change;

        /// <summary>Content-modification timestamp; retained only to preserve native field offsets.</summary>
        public Timestamp Modify;

        /// <summary>Device-file major number; retained only to preserve native field offsets.</summary>
        public uint RdevMajor;

        /// <summary>Device-file minor number; retained only to preserve native field offsets.</summary>
        public uint RdevMinor;

        /// <summary>Major number of the filesystem containing the file.</summary>
        public uint DeviceMajor;

        /// <summary>Minor number of the filesystem containing the file.</summary>
        public uint DeviceMinor;

        // Linux reserves the remaining words for future statx extensions. They must remain in the
        // managed layout even though houseCARL does not interpret them.
        public ulong Spare1, Spare2, Spare3, Spare4, Spare5, Spare6, Spare7;
        public ulong Spare8, Spare9, Spare10, Spare11, Spare12, Spare13, Spare14;
    }
}

/// <summary>One staged write awaiting a later Amethyst filemap rebuild and deployment.</summary>
public sealed class PendingAmethystWrite
{
    /// <summary>Amethyst profile that owned the staging tree when the write occurred.</summary>
    public required string ProfileName { get; set; }

    /// <summary>Absolute native path written inside the active Amethyst staging tree.</summary>
    public required string StagingPath { get; set; }

    /// <summary>Canonical Bethesda Data-relative path represented by <see cref="StagingPath"/>.</summary>
    public required string DataRelativePath { get; set; }

    /// <summary>SHA-256 of the staged output immediately after the successful write.</summary>
    public required string ContentSha256 { get; set; }

    /// <summary>UTC completion time used as the lower bound for later manager-state files.</summary>
    public required DateTime WrittenUtc { get; set; }

    /// <summary>Human-readable write category used in status and diagnostics.</summary>
    public required string Kind { get; set; }

    /// <summary>Identity of the new staged file after atomic replacement, when Linux exposed it.</summary>
    public LinuxFileIdentity? StagingIdentity { get; set; }

    /// <summary>Identity of an existing staged file before atomic replacement, when one existed.</summary>
    public LinuxFileIdentity? PreviousStagingIdentity { get; set; }

    /// <summary>
    /// Whether deployed Data and staging were the same hardlink before replacement. Null means the
    /// relationship could not be measured, not that the files were different.
    /// </summary>
    public bool? DeployedWasSameHardlink { get; set; }
}

/// <summary>Pure helpers for recording and verifying staged deployment state.</summary>
public static class AmethystRedeploy
{
    /// <summary>Computes the uppercase SHA-256 used to identify an exact staged result.</summary>
    /// <param name="path">Native path of the file to read.</param>
    /// <returns>The complete content digest as hexadecimal text.</returns>
    /// <exception cref="IOException">The file cannot be read completely.</exception>
    public static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>Determines whether a later Amethyst deployment made one pending write game-visible.</summary>
    /// <param name="pending">The staged write and content identity recorded at write time.</param>
    /// <param name="snapshot">Fresh manager state captured after the write.</param>
    /// <returns>
    /// True only when the same profile is actively deployed, both manager-state files are newer,
    /// the filemap still selects the recorded staging source, staging has not changed, and deployed
    /// Data is either the same hardlink or has identical content.
    /// </returns>
    /// <remarks>Every missing, stale, or unreadable input returns false so verification fails closed.</remarks>
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

    /// <summary>Checks that a manager-owned freshness input was observed after the staged write.</summary>
    static bool Newer(ManagerSnapshot snapshot, string path, DateTime written) =>
        snapshot.FreshnessInputs.TryGetValue(path, out var stamp) && stamp > written;

    /// <summary>Compares two native paths after resolving relative segments without touching either file.</summary>
    /// <remarks>An invalid path returns false because source identity must never be guessed.</remarks>
    static bool SamePath(string left, string right)
    {
        try { return Path.GetFullPath(left) == Path.GetFullPath(right); }
        catch { return false; }
    }
}
