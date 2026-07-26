using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// Adapts inherited directory fixtures to manager-neutral service roots.
/// The temporary legacy parser dependency is removed when those fixtures stop writing ModOrganizer.ini.
/// </summary>
internal static class SyntheticManagerFixture
{
    /// <summary>Opens one inherited fixture without enabling any manager-specific product mode.</summary>
    /// <param name="instanceDir">Fixture root containing its legacy path-description file.</param>
    /// <param name="maxPlugins">Optional positive resolver cap; zero means unlimited.</param>
    /// <param name="store">Isolated configuration store for the probe.</param>
    /// <returns>A service configured only with derived native roots.</returns>
    internal static LoadOrderService Open(
        string instanceDir, int maxPlugins, UserConfigStore store)
    {
        var paths = LegacyFixturePaths.Resolve(instanceDir);
        return LoadOrderService.WithFixturePaths(
            paths.DataDir, paths.ModsDir, paths.ProfileDir, paths.OverwriteDir,
            Path.GetDirectoryName(paths.ProfileDir), maxPlugins, store);
    }

    /// <summary>Repoints a running service to another inherited synthetic fixture.</summary>
    /// <param name="service">Fixture service whose caches must be invalidated.</param>
    /// <param name="instanceDir">Replacement fixture root.</param>
    internal static void Switch(LoadOrderService service, string instanceDir)
    {
        var paths = LegacyFixturePaths.Resolve(instanceDir);
        service.SwitchFixturePaths(
            paths.DataDir, paths.ModsDir, paths.ProfileDir, paths.OverwriteDir,
            Path.GetDirectoryName(paths.ProfileDir));
    }
}
