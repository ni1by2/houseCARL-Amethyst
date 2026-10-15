using HousecarlCore;

namespace HousecarlMcp;

/// <summary>Manager-specific next-step language for outputs produced under a pinned manager snapshot.</summary>
internal static class ManagerPresentation
{
    static bool IsAmethyst => AmethystSnapshotContext.Current is not null;

    public static string NewPluginFolder(string modFolder) => IsAmethyst
        ? $"mod folder: {modFolder} — review the patch, activate it in Amethyst's selected profile, then Refresh and Deploy; the selected plugin order determines conflicts.\n"
        : $"mod folder: {modFolder}  — enable it in MO2 to use the patch; MO2 adds a newly activated plugin at the END of the load order, so it overrides what is already there\n";

    public static string ExistingPluginFolder(string modFolder) => IsAmethyst
        ? $"mod folder: {modFolder} — the patch folder is already active in Amethyst's selected profile; Refresh and Deploy to apply this staged update.\n"
        : $"mod folder: {modFolder}  — already active in your load order; re-sort only if a winner changed\n";

    public static string MergedPluginFolder(string modFolder) => IsAmethyst
        ? $"mod folder: {modFolder} — review in xEdit, activate the plugin in Amethyst's selected profile, then Refresh and Deploy; plugin order determines conflicts.\n"
        : $"mod folder: {modFolder}  — review in xEdit, then enable it in MO2 (MO2 adds a newly activated plugin at the END of the load order).\n";

    public static string NewModFolder(string modFolder, bool requiresPlugin = false) => IsAmethyst
        ? requiresPlugin
            ? $"mod folder: {modFolder} — activate the matching plugin and this folder in Amethyst's selected profile, then Refresh and Deploy.\n"
            : $"mod folder: {modFolder} — review it in Amethyst, then Refresh and Deploy the selected profile to make these files available.\n"
        : requiresPlugin
            ? $"mod folder: {modFolder} — enable it in MO2 (and enable its matching plugin) to use the output.\n"
            : $"mod folder: {modFolder}  — enable it in MO2 to use the output\n";

    public static string NewFolderFiles(bool requiresPlugin = false) => IsAmethyst
        ? requiresPlugin
            ? "(staged in a houseCARL folder; activate its matching plugin in Amethyst's selected profile, then Refresh and Deploy.)"
            : "(staged in a houseCARL folder; review it in Amethyst, then Refresh and Deploy the selected profile.)"
        : requiresPlugin
            ? "(a new houseCARL mod folder — enable it and its matching plugin in MO2 to use the output.)"
            : "(a new houseCARL mod folder — enable it in MO2 to use the output.)";

    public static string ManagerName => IsAmethyst ? "Amethyst" : "MO2";
}
