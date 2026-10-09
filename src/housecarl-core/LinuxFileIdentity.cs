using System.Runtime.InteropServices;

namespace HousecarlCore;

/// <summary>Linux device/inode and content stamps; replacement with identical bytes still changes identity.</summary>
/// <param name="DeviceMajor">Filesystem device major number.</param>
/// <param name="DeviceMinor">Filesystem device minor number.</param>
/// <param name="Inode">File identity on that filesystem.</param>
/// <param name="Size">File length in bytes.</param>
/// <param name="MtimeNs">Last-write time in nanoseconds since the Unix epoch.</param>
public readonly record struct LinuxFileIdentity(uint DeviceMajor, uint DeviceMinor, ulong Inode, ulong Size, long MtimeNs)
{
    /// <summary>Read one existing entry with Linux statx, following symlinks. Failure is always named.</summary>
    /// <param name="path">Absolute native path to an existing file or directory.</param>
    /// <returns>Identity and content stamps, without opening a write handle.</returns>
    public static LinuxFileIdentity Of(string path)
    {
        var buffer = new byte[256];
        if (Statx(-100, path, 0, 0x7ff, buffer) != 0)
            throw new IOException($"cannot inspect '{path}' (statx errno {Marshal.GetLastPInvokeError()}); refresh Amethyst and retry");
        if ((BitConverter.ToUInt32(buffer, 0) & 0x340) != 0x340)
            throw new IOException($"statx did not return identity, size and modification time for '{path}'");
        return new(BitConverter.ToUInt32(buffer, 136), BitConverter.ToUInt32(buffer, 140),
            BitConverter.ToUInt64(buffer, 32), BitConverter.ToUInt64(buffer, 40),
            checked(BitConverter.ToInt64(buffer, 112) * 1_000_000_000L + BitConverter.ToUInt32(buffer, 120)));
    }

    /// <summary>Resolve all symlinked path components using the same canonical identity as Python Path.resolve.</summary>
    /// <param name="path">Absolute existing native path.</param>
    /// <returns>The absolute physical path; errors refuse rather than selecting a guessed root.</returns>
    public static string Canonical(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new IOException($"an absolute native path is required: '{path}'");
        var pointer = Realpath(path, IntPtr.Zero);
        if (pointer == IntPtr.Zero) throw new IOException($"cannot resolve '{path}' (realpath errno {Marshal.GetLastPInvokeError()})");
        try { return Marshal.PtrToStringUTF8(pointer) ?? throw new IOException($"cannot decode resolved path '{path}'"); }
        finally { Free(pointer); }
    }

    /// <summary>Linux statx ABI: the output layout is fixed at 256 bytes.</summary>
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    static extern int Statx(int fd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, [Out] byte[] result);
    /// <summary>Allocate the canonical path using libc.</summary>
    [DllImport("libc", EntryPoint = "realpath", SetLastError = true)]
    static extern IntPtr Realpath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr destination);
    /// <summary>Release the buffer allocated by realpath.</summary>
    [DllImport("libc", EntryPoint = "free")]
    static extern void Free(IntPtr pointer);
}
