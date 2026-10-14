using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Security.Cryptography;

namespace HousecarlCore;

/// <summary>Linux openat2 based filesystem access. This intentionally has no path-based fallback.</summary>
internal static class LinuxAnchoredFileSystem
{
    const ulong ResolveNoMagicLinks = 0x02;
    const ulong ResolveNoSymlinks = 0x04;
    const ulong ResolveBeneath = 0x08;
    const ulong Resolve = ResolveNoMagicLinks | ResolveNoSymlinks | ResolveBeneath;
    const long SysOpenAt2 = 437; // Linux x86_64 and arm64
    const int ENoEnt = 2;
    const int EExist = 17;
    const int OWriteOnly = 1;
    const int OReadWrite = 2;
    const int OCreate = 0x40;
    const int OExclusive = 0x80;
    const int ODirectory = 0x10000;
    const int ONoFollow = 0x20000;
    const int OCloExec = 0x80000;
    const uint Mode0700 = 0x1c0;
    const uint Mode0600 = 0x180;
    const uint RenameNoReplace = 1;
    const uint RenameExchange = 2;

    [StructLayout(LayoutKind.Sequential)] struct OpenHow { public ulong flags, mode, resolve; }

    internal static SafeFileHandle OpenRoot()
    {
        EnsureSupported();
        int fd = Native.open("/", Native.O_RDONLY | ODirectory | OCloExec | ONoFollow, 0);
        if (fd < 0) ThrowErrno("open filesystem root");
        return Handle(fd);
    }

    internal static SafeFileHandle OpenAbsoluteDirectory(string path, bool create = false)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("managed Amethyst writes require Linux openat2");
        if (!Path.IsPathFullyQualified(path)) throw new IOException("anchored directory path must be absolute");
        using var root = OpenRoot();
        var components = Path.GetFullPath(path).Split('/', StringSplitOptions.RemoveEmptyEntries);
        SafeFileHandle current = Handle(Native.dup(root.DangerousGetHandle().ToInt32()));
        if (current.IsInvalid) ThrowErrno("duplicate filesystem root descriptor");
        try
        {
            foreach (var component in components)
            {
                if (create) TryMkdirAt(current, component, Mode0700);
                var next = OpenDirectoryAt(current, component);
                current.Dispose(); current = next;
            }
            return current;
        }
        catch { current.Dispose(); throw; }
    }

    internal static SafeFileHandle OpenDirectoryAt(SafeFileHandle parent, string name)
        => OpenAt(parent, name, Native.O_RDONLY | ODirectory | OCloExec | ONoFollow, 0);

    internal static SafeFileHandle OpenFileAt(SafeFileHandle parent, string name, bool write = false, bool createNew = false)
        => OpenAt(parent, name, (write ? OWriteOnly : Native.O_RDONLY) | OCloExec | ONoFollow |
            (createNew ? OCreate | OExclusive : 0), createNew ? Mode0600 : 0);

    internal static SafeFileHandle TryOpenFileAt(SafeFileHandle parent, string name)
    {
        ValidateName(name); EnsureSupported();
        var how = new OpenHow { flags = Native.O_RDONLY | OCloExec | ONoFollow, resolve = Resolve };
        long fd = Native.syscall(SysOpenAt2, parent.DangerousGetHandle().ToInt32(), name, ref how, (nuint)Marshal.SizeOf<OpenHow>());
        if (fd >= 0) return Handle(checked((int)fd));
        int error = Marshal.GetLastPInvokeError();
        if (error == ENoEnt) return new SafeFileHandle(new IntPtr(-1), ownsHandle: true);
        ThrowErrno($"openat2 '{name}'", error); return null!;
    }

    internal static SafeFileHandle OpenRelativeDirectory(SafeFileHandle root, string relative, bool create = false)
    {
        ValidateRelative(relative);
        var parts = relative.Split('/');
        SafeFileHandle current = Handle(Native.dup(root.DangerousGetHandle().ToInt32()));
        if (current.IsInvalid) ThrowErrno("duplicate anchored directory descriptor");
        try
        {
            foreach (var part in parts)
            {
                if (create) TryMkdirAt(current, part, Mode0700);
                var next = OpenDirectoryAt(current, part);
                current.Dispose(); current = next;
            }
            return current;
        }
        catch { current.Dispose(); throw; }
    }

    internal static void CreateDirectories(SafeFileHandle root, string relative)
    {
        if (string.IsNullOrEmpty(relative) || relative == ".") return;
        using var _ = OpenRelativeDirectory(root, relative, create: true);
    }

    internal static void TryMkdirAt(SafeFileHandle parent, string name, uint mode)
    {
        ValidateName(name);
        if (Native.mkdirat(parent.DangerousGetHandle().ToInt32(), name, mode) == 0) return;
        int error = Marshal.GetLastPInvokeError();
        if (error != EExist) ThrowErrno($"create anchored directory '{name}'", error);
        // Existing entries are opened with openat2 by the caller; symlinks and non-directories fail closed.
    }

    internal static void Rename(SafeFileHandle oldParent, string oldName, SafeFileHandle newParent, string newName, bool noReplace = false)
    {
        ValidateName(oldName); ValidateName(newName);
        if (Native.renameat2(oldParent.DangerousGetHandle().ToInt32(), oldName,
                newParent.DangerousGetHandle().ToInt32(), newName, noReplace ? RenameNoReplace : 0) != 0)
            ThrowErrno("descriptor-relative atomic rename");
    }

    internal static void Exchange(SafeFileHandle parent, string first, string second)
    {
        ValidateName(first); ValidateName(second);
        if (Native.renameat2(parent.DangerousGetHandle().ToInt32(), first, parent.DangerousGetHandle().ToInt32(), second, RenameExchange) != 0)
            ThrowErrno("descriptor-relative atomic exchange");
    }

    internal static void Unlink(SafeFileHandle parent, string name)
    {
        ValidateName(name);
        if (Native.unlinkat(parent.DangerousGetHandle().ToInt32(), name, 0) != 0) ThrowErrno("descriptor-relative unlink");
    }

    internal static void Sync(SafeFileHandle handle)
    {
        if (Native.fsync(handle.DangerousGetHandle().ToInt32()) != 0) ThrowErrno("fsync anchored descriptor");
    }

    internal static void SetMode(SafeFileHandle handle, uint mode)
    {
        if (Native.fchmod(handle.DangerousGetHandle().ToInt32(), mode) != 0) ThrowErrno("fchmod anchored descriptor");
    }

    internal static string Hash(SafeFileHandle handle)
    {
        using var stream = Stream(handle, FileAccess.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    internal static LinuxFileIdentity Identity(SafeFileHandle handle)
    {
        var buffer = new byte[256];
        if (Native.statx(handle.DangerousGetHandle().ToInt32(), "", 0x1000, 0x7ff, buffer) != 0)
            ThrowErrno("statx anchored descriptor");
        if ((BitConverter.ToUInt32(buffer, 0) & 0x340) != 0x340) throw new IOException("statx omitted identity or content stamps for anchored descriptor");
        return new(BitConverter.ToUInt32(buffer, 136), BitConverter.ToUInt32(buffer, 140),
            BitConverter.ToUInt64(buffer, 32), BitConverter.ToUInt64(buffer, 40),
            checked(BitConverter.ToInt64(buffer, 112) * 1_000_000_000L + BitConverter.ToUInt32(buffer, 120)));
    }

    internal static FileStream Stream(SafeFileHandle handle, FileAccess access)
    {
        int fd = Native.dup(handle.DangerousGetHandle().ToInt32());
        if (fd < 0) ThrowErrno("duplicate anchored file descriptor");
        var stream = new FileStream(Handle(fd), access, 128 * 1024, isAsync: false);
        stream.Position = 0;
        return stream;
    }

    static SafeFileHandle OpenAt(SafeFileHandle directory, string path, int flags, uint mode)
    {
        ValidateRelative(path);
        EnsureSupported();
        var how = new OpenHow { flags = (uint)flags, mode = mode, resolve = Resolve };
        long fd = Native.syscall(SysOpenAt2, directory.DangerousGetHandle().ToInt32(), path, ref how, (nuint)Marshal.SizeOf<OpenHow>());
        if (fd < 0) ThrowErrno($"openat2 '{path}'");
        return Handle(checked((int)fd));
    }

    static void EnsureSupported()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("managed Amethyst writes require Linux openat2");
        if (RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
            throw new PlatformNotSupportedException($"managed Amethyst writes require a verified openat2 syscall ABI; {RuntimeInformation.ProcessArchitecture} is unsupported");
        using var root = Handle(Native.open("/", Native.O_RDONLY | ODirectory | OCloExec, 0));
        var how = new OpenHow { flags = Native.O_RDONLY | OCloExec, resolve = Resolve };
        long fd = Native.syscall(SysOpenAt2, root.DangerousGetHandle().ToInt32(), ".", ref how, (nuint)Marshal.SizeOf<OpenHow>());
        if (fd < 0)
        {
            int errno = Marshal.GetLastPInvokeError();
            if (errno is 38 or 22 or 95) throw new PlatformNotSupportedException($"kernel openat2 no-follow resolution is required for managed Amethyst writes (errno {errno})");
            ThrowErrno("probe openat2", errno);
        }
        Native.close(checked((int)fd));
    }

    static void ValidateRelative(string path)
    {
        if (string.IsNullOrEmpty(path) || Path.IsPathFullyQualified(path) || path.Contains('\0') || path.Contains('\\') ||
            path.Split('/').Any(p => p.Length == 0 || p is "." or ".."))
            throw new IOException("unsafe descriptor-relative path");
    }

    static void ValidateName(string name)
    {
        if (string.IsNullOrEmpty(name) || name is "." or ".." || name.Contains('/') || name.Contains('\\') || name.Contains('\0'))
            throw new IOException("unsafe descriptor-relative leaf name");
    }

    static SafeFileHandle Handle(int fd)
    {
        if (fd < 0) ThrowErrno("open descriptor");
        return new SafeFileHandle((IntPtr)fd, ownsHandle: true);
    }

    static void ThrowErrno(string operation, int? error = null)
    {
        int errno = error ?? Marshal.GetLastPInvokeError();
        throw new IOException($"{operation} failed (errno {errno})");
    }

    static class Native
    {
        internal const int O_RDONLY = 0, O_CLOEXEC = 0x80000, O_DIRECTORY = 0x10000;
        [DllImport("libc", EntryPoint = "open", SetLastError = true)] internal static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mode);
        [DllImport("libc", EntryPoint = "dup", SetLastError = true)] internal static extern int dup(int fd);
        [DllImport("libc", EntryPoint = "close", SetLastError = true)] internal static extern int close(int fd);
        [DllImport("libc", EntryPoint = "mkdirat", SetLastError = true)] internal static extern int mkdirat(int fd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);
        [DllImport("libc", EntryPoint = "renameat2", SetLastError = true)] internal static extern int renameat2(int oldfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string oldpath, int newfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string newpath, uint flags);
        [DllImport("libc", EntryPoint = "unlinkat", SetLastError = true)] internal static extern int unlinkat(int fd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
        [DllImport("libc", EntryPoint = "fsync", SetLastError = true)] internal static extern int fsync(int fd);
        [DllImport("libc", EntryPoint = "fchmod", SetLastError = true)] internal static extern int fchmod(int fd, uint mode);
        [DllImport("libc", EntryPoint = "statx", SetLastError = true)] internal static extern int statx(int fd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, [Out] byte[] result);
        [DllImport("libc", EntryPoint = "syscall", SetLastError = true)] internal static extern long syscall(long number, int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, ref OpenHow how, nuint size);
    }
}
