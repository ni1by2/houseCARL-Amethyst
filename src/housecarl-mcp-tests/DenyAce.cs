using System.Security.AccessControl;
using System.Security.Principal;
using System.Collections.Concurrent;

namespace HousecarlMcpTests;

/// <summary>The deny-ACE machinery the unreadable-root worlds share: deny the current account one directory, verify the
/// deny actually bit on this host rather than trusting the call, and take it off again. One copy, so a host where a
/// deny stops biting is a single fix; the worlds that use it stage their own content.</summary>
static class DenyAce
{
    static readonly ConcurrentDictionary<string, UnixFileMode> OriginalModes = new();
    /// <summary>Deny the current user everything on one directory. False means the ACE did not bite on this host, and
    /// it has already been taken back off — a fixture that did not build is a failure, never a quiet pass.</summary>
    public static bool TryDeny(string dir, bool listingOnly = false)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                OriginalModes.TryAdd(dir, File.GetUnixFileMode(dir));
                File.SetUnixFileMode(dir, listingOnly
                    ? OriginalModes[dir] & ~(UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead)
                    : UnixFileMode.None);
                try { Directory.EnumerateFiles(dir).ToList(); }
                catch (UnauthorizedAccessException) { return true; }
                Undeny(dir);
                return false;
            }
            var me = WindowsIdentity.GetCurrent().Name;
            var di = new DirectoryInfo(dir);
            var sec = di.GetAccessControl();
            sec.AddAccessRule(new FileSystemAccessRule(me, listingOnly ? FileSystemRights.ListDirectory : FileSystemRights.FullControl,
                listingOnly ? InheritanceFlags.None : InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Deny));
            di.SetAccessControl(sec);
            try { Directory.EnumerateFiles(dir, "*").ToList(); } catch (UnauthorizedAccessException) { return true; } catch { }
            Undeny(dir);
            return false;
        }
        catch { return false; }
    }

    /// <summary>Take the deny ACE off again — left behind, it would block the temp tree's own cleanup.</summary>
    public static void Undeny(string dir)
    {
        if (!OperatingSystem.IsWindows())
        {
            if (OriginalModes.TryRemove(dir, out var original)) File.SetUnixFileMode(dir, original);
            return;
        }
        try
        {
            var me = WindowsIdentity.GetCurrent().Name;
            var di = new DirectoryInfo(dir);
            var sec = di.GetAccessControl();
            sec.RemoveAccessRuleAll(new FileSystemAccessRule(me, FileSystemRights.FullControl, AccessControlType.Deny));
            sec.RemoveAccessRuleAll(new FileSystemAccessRule(me, FileSystemRights.ListDirectory, AccessControlType.Deny));
            di.SetAccessControl(sec);
        }
        catch { /* cleanup is best effort; the temp tree goes either way */ }
    }
}
