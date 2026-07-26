using System.Runtime.InteropServices;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>Locks the staging-first redeploy gate for hardlink, symlink, and copy deployments.</summary>
internal static class AmethystRedeployProbe
{
    /// <summary>Runs all redeployment safety scenarios and returns a process-style success code.</summary>
    /// <param name="args">Reserved for the common probe entry-point contract; currently unused.</param>
    /// <returns>Zero on success or on non-Linux hosts; one when a scenario fails.</returns>
    public static int RunGuard(string[] args)
    {
        if (!OperatingSystem.IsLinux()) return 0;
        var root = Path.Combine(Path.GetTempPath(), "hc-amethyst-redeploy-" + Guid.NewGuid().ToString("N"));
        try
        {
            HardlinkReplacement(root);
            SmokeModes(root);
            PublicGate();
            Console.WriteLine("PASS: Amethyst redeploy verification and deployment-mode smoke tests");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL: " + ex); return 1; }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    /// <summary>
    /// Proves that atomic staging replacement leaves the deployed hardlink on its old inode and
    /// that only a newer, matching Amethyst deployment can clear the pending marker.
    /// </summary>
    static void HardlinkReplacement(string root)
    {
        var f = Fixture(Path.Combine(root, "hardlink"));
        File.WriteAllText(f.Staging, "old");
        Check(link(f.Staging, f.Deployed) == 0, "create hardlink");
        var before = LinuxFileIdentity.Read(f.Staging);
        Check(before == LinuxFileIdentity.Read(f.Deployed), "deployed file starts as the staging hardlink");

        AtomicFile.WriteAllBytes(f.Staging, "new"u8.ToArray());
        var pending = Pending(f, before, true);
        Check(LinuxFileIdentity.Read(f.Staging) != before, "atomic staging write replaces the inode");
        Check(File.ReadAllText(f.Deployed) == "old", "deployed hardlink honestly remains on the old inode");
        Check(!AmethystRedeploy.IsVerified(pending, Snapshot(f, pending, active: true, fresh: false)), "stale manager state cannot clear pending");
        Check(!AmethystRedeploy.IsVerified(pending, Snapshot(f, pending, active: false, fresh: true)), "inactive deployment cannot clear pending");
        Check(!AmethystRedeploy.IsVerified(pending,
            Snapshot(f, pending, active: true, fresh: true) with { ActiveProfileName = "Other" }),
            "another active profile cannot clear pending");

        File.Delete(f.Deployed);
        Check(link(f.Staging, f.Deployed) == 0, "redeploy hardlink");
        var verified = Snapshot(f, pending, active: true, fresh: true);
        Check(AmethystRedeploy.IsVerified(pending, verified), "later hardlink deployment verifies");

        var store = new UserConfigStore(Path.Combine(f.Root, "user.json"));
        Check(store.RecordPendingAmethystWrite(pending).ok, "persist pending write");
        Check(store.VerifyPendingAmethystWrites(Snapshot(f, pending, active: false, fresh: true)).remaining == 1,
            "inactive deployment retains stored pending state");
        Check(store.VerifyPendingAmethystWrites(verified) is { cleared: 1, remaining: 0 },
            "verified later deployment clears stored pending state");

        File.WriteAllText(f.Staging, "changed outside houseCARL");
        Check(!AmethystRedeploy.IsVerified(pending, verified), "changed staging content cannot clear an older pending write");
    }

    /// <summary>Confirms that the content fallback also recognizes valid symlink and copy deployments.</summary>
    static void SmokeModes(string root)
    {
        foreach (var mode in new[] { "symlink", "copy" })
        {
            var f = Fixture(Path.Combine(root, mode));
            File.WriteAllText(f.Staging, mode);
            if (mode == "symlink") File.CreateSymbolicLink(f.Deployed, f.Staging);
            else File.Copy(f.Staging, f.Deployed);
            var pending = Pending(f, null, null);
            Check(AmethystRedeploy.IsVerified(pending, Snapshot(f, pending, active: true, fresh: true)), $"{mode} verification");
        }
    }

    /// <summary>Guards every public in-place tool against losing its explicit redeployment parameter.</summary>
    static void PublicGate()
    {
        foreach (var method in new[] { "SetField", "BulkApply", "RemoveRecord", "CreateRecord", "BulkCreate", "ForwardRecord", "CompactPlugin" })
        {
            var info = typeof(HousecarlMcp.WriteTools).GetMethod(method)!;
            var gate = info.GetParameters().SingleOrDefault(p => p.Name == "confirm_amethyst_redeploy");
            Check(gate is not null && gate.HasDefaultValue && Equals(gate.DefaultValue, false), $"{method} exposes an opt-in redeploy gate");
        }
        var nif = typeof(HousecarlMcp.NifTools).GetMethod("NifSet")!.GetParameters()
            .SingleOrDefault(p => p.Name == "confirm_amethyst_redeploy");
        Check(nif is not null && nif.HasDefaultValue && Equals(nif.DefaultValue, false), "NifSet exposes an opt-in redeploy gate");
    }

    /// <summary>Creates an isolated staging/Data layout and manager-state files for one scenario.</summary>
    static FixtureData Fixture(string root)
    {
        var mods = Path.Combine(root, "mods");
        var game = Path.Combine(root, "game");
        var staging = Path.Combine(mods, "Test", "Meshes", "Test.nif");
        var deployed = Path.Combine(game, "Data", "Meshes", "Test.nif");
        Directory.CreateDirectory(Path.GetDirectoryName(staging)!);
        Directory.CreateDirectory(Path.GetDirectoryName(deployed)!);
        var filemap = Path.Combine(root, "filemap.txt");
        var deploy = Path.Combine(root, "deploy_state.json");
        File.WriteAllText(filemap, "");
        File.WriteAllText(deploy, "{}");
        return new(root, mods, game, staging, deployed, filemap, deploy);
    }

    /// <summary>Records the fixture's current staged content as a pending write.</summary>
    static PendingAmethystWrite Pending(FixtureData f, LinuxFileIdentity? before, bool? sameHardlink) => new()
    {
        ProfileName = "Default", StagingPath = f.Staging, DataRelativePath = @"Meshes\Test.nif",
        ContentSha256 = AmethystRedeploy.Hash(f.Staging), WrittenUtc = DateTime.UtcNow,
        Kind = "test", StagingIdentity = LinuxFileIdentity.Read(f.Staging), PreviousStagingIdentity = before,
        DeployedWasSameHardlink = sameHardlink
    };

    /// <summary>Builds the minimum manager snapshot needed to test redeployment verification.</summary>
    static ManagerSnapshot Snapshot(FixtureData f, PendingAmethystWrite pending, bool active, bool fresh)
    {
        var stamp = fresh ? pending.WrittenUtc.AddSeconds(1) : pending.WrittenUtc.AddSeconds(-1);
        return new("connection.json", 1, "Default", f.Root, false, f.Mods, Path.Combine(f.Root, "overwrite"),
            f.Filemap, Path.Combine(f.Root, "modindex.bin"), f.Game, Path.Combine(f.Game, "Data_Core"),
            Path.Combine(f.Root, "paths.json"), f.DeployState, active, "hardlink", true,
            Array.Empty<string>(), new Dictionary<string, string>(),
            new Dictionary<string, ManagerFileSource>(StringComparer.OrdinalIgnoreCase)
            { [pending.DataRelativePath] = new("Test", f.Staging) },
            Array.Empty<string>(), new Dictionary<string, DateTime>
            { [f.Filemap] = stamp, [f.DeployState] = stamp });
    }

    /// <summary>Turns a failed safety invariant into a probe failure with a readable reason.</summary>
    static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    /// <summary>Calls Linux <c>link(2)</c> to create a real hardlink without invoking a shell utility.</summary>
    [DllImport("libc", SetLastError = true)] static extern int link(string oldpath, string newpath);

    /// <summary>All native paths owned by one isolated redeployment test fixture.</summary>
    /// <param name="Root">Temporary directory containing the entire fixture.</param>
    /// <param name="Mods">Effective Amethyst mods staging root.</param>
    /// <param name="Game">Synthetic Skyrim installation root.</param>
    /// <param name="Staging">Physical winning file inside the staging mod.</param>
    /// <param name="Deployed">Corresponding game-visible file under Data.</param>
    /// <param name="Filemap">Synthetic Amethyst filemap freshness input.</param>
    /// <param name="DeployState">Synthetic Amethyst deployment-state freshness input.</param>
    sealed record FixtureData(string Root, string Mods, string Game, string Staging, string Deployed, string Filemap, string DeployState);
}
