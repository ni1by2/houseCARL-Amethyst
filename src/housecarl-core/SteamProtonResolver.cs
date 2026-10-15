using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace HousecarlCore;

/// <summary>The currently selected native-Steam compatibility tool and the exact selected profile prefix.</summary>
public sealed record SteamProtonSelection(string SteamRoot, string LibraryRoot, string ToolName,
    string ToolDirectory, string ProtonExecutable, string PrefixPath, string CompatDataPath, string AppId)
{
    /// <summary>Apply the runtime contract Proton uses when Steam launches a compatibility tool for this game.</summary>
    public void Configure(ProcessStartInfo startInfo)
    {
        var target = startInfo.FileName;
        var arguments = startInfo.ArgumentList.ToArray();
        startInfo.Environment["STEAM_COMPAT_CLIENT_INSTALL_PATH"] = SteamRoot;
        startInfo.Environment["STEAM_COMPAT_DATA_PATH"] = CompatDataPath;
        startInfo.Environment["STEAM_COMPAT_APP_ID"] = AppId;
        startInfo.Environment["SteamAppId"] = AppId;
        startInfo.Environment["WINEPREFIX"] = PrefixPath;
        startInfo.WorkingDirectory = ToolDirectory;
        startInfo.FileName = ProtonExecutable;
        startInfo.ArgumentList.Clear();
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add(target);
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
    }

    /// <summary>Translate an existing host path, or a path with a missing leaf, through this prefix's drive links.</summary>
    public string TranslatePath(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new InvalidOperationException($"Proton argument is not an absolute host path: '{path}'");
        var canonical = CanonicalAllowMissingLeaf(path);
        var drives = Path.Combine(PrefixPath, "dosdevices");
        if (!Directory.Exists(drives) || new DirectoryInfo(drives).LinkTarget is not null)
            throw new InvalidOperationException($"selected Proton prefix has no safe dosdevices directory: '{drives}'");
        var maps = new List<(string Letter, string Root)>();
        foreach (var entry in Directory.EnumerateFileSystemEntries(drives))
        {
            var name = Path.GetFileName(entry);
            if (name.Length != 2 || name[1] != ':' || !char.IsAsciiLetter(name[0])) continue;
            var info = Directory.Exists(entry) ? (FileSystemInfo)new DirectoryInfo(entry) : new FileInfo(entry);
            if (info.LinkTarget is null) continue;
            try { maps.Add((name[..1].ToUpperInvariant(), LinuxFileIdentity.Canonical(entry))); }
            catch (IOException) { /* broken drive links are not usable mappings */ }
        }
        var duplicateDrive = maps.GroupBy(m => m.Letter, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicateDrive is not null)
            throw new InvalidOperationException($"selected Proton prefix has multiple host mappings for drive {duplicateDrive.Key}; refusing ambiguous Windows path translation");
        var match = maps.OrderByDescending(m => m.Root.Length).FirstOrDefault(m => IsUnder(m.Root, canonical));
        if (match.Root is null)
            throw new InvalidOperationException($"host path '{path}' is not reachable through a drive mapping in the selected Proton prefix; refusing to launch with an unmapped argument");
        var relative = Path.GetRelativePath(match.Root, canonical);
        return match.Letter + ":\\" + (relative == "." ? "" : relative.Replace(Path.DirectorySeparatorChar, '\\'));
    }

    static string CanonicalAllowMissingLeaf(string path)
    {
        var full = Path.GetFullPath(path);
        if (File.Exists(full) || Directory.Exists(full)) return LinuxFileIdentity.Canonical(full);
        var parent = Path.GetDirectoryName(full);
        if (parent is null || parent == full) throw new InvalidOperationException($"cannot resolve the parent of Proton argument '{path}'");
        return Path.Combine(CanonicalAllowMissingLeaf(parent), Path.GetFileName(full));
    }

    static bool IsUnder(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative == "." || (relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }
}

/// <summary>Read-only native Steam selection resolver for the pinned Skyrim SE app. Never uses prefix config_info:
/// it identifies the Proton build that created a prefix, not necessarily Steam's current app assignment.</summary>
public static class SteamProtonResolver
{
    public const string SkyrimSeAppId = "489830";
    static readonly Regex Tokens = new("\\s*(?:\"((?:\\\\.|[^\"])*)\"|([{}])|//[^\\r\\n]*)", RegexOptions.Compiled);

    sealed record VdfValue(string? Text, Dictionary<string, VdfValue>? Children);

    /// <summary>Resolve selected app mapping and its installed tool from the selected Amethyst prefix.</summary>
    public static SteamProtonSelection Resolve(AmethystSavedSnapshot snapshot, string? homeOverride = null) =>
        Resolve(snapshot, homeOverride, ["/usr/share/steam/compatibilitytools.d", "/usr/local/share/steam/compatibilitytools.d"]);

    internal static SteamProtonSelection Resolve(AmethystSavedSnapshot snapshot, string? homeOverride,
        IReadOnlyList<string> systemToolRoots)
    {
        if (!OperatingSystem.IsLinux()) throw new InvalidOperationException("Steam Proton selection is supported only on Linux");
        if (snapshot.PrefixPath is not { Length: > 0 } prefix) throw new InvalidOperationException("Amethyst has no effective prefix_path in the selected schema-v1 manifest/profile settings");
        var home = Path.GetFullPath(homeOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var rootCandidates = new[] { Path.Combine(home, ".steam", "steam"), Path.Combine(home, ".local", "share", "Steam") }
            .Where(Directory.Exists).Select(CanonicalDirectory).Distinct(PathComparer).ToArray();
        if (rootCandidates.Length == 0)
            throw new InvalidOperationException("native Steam client root was not found in ~/.steam/steam or ~/.local/share/Steam; Flatpak and custom client layouts are unsupported");
        if (rootCandidates.Length > 1)
            throw new InvalidOperationException("multiple distinct native Steam client roots were found; refusing ambiguous CompatToolMapping selection");
        var steamRoot = rootCandidates[0];
        var configPath = Path.Combine(steamRoot, "config", "config.vdf");
        var librariesPath = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        var config = ReadVdf(configPath, "Steam config.vdf");
        var appConfig = Child(config, "InstallConfigStore");
        var software = Child(appConfig, "Software");
        var valve = Child(software, "Valve");
        var steam = Child(valve, "Steam");
        var mapping = Child(steam, "CompatToolMapping");
        if (!mapping.TryGetValue(SkyrimSeAppId, out var selected) || selected.Children is null)
            throw new InvalidOperationException($"Steam config.vdf has no current CompatToolMapping for Skyrim SE app {SkyrimSeAppId}; refusing to infer from prefix config_info");
        var toolName = Text(selected.Children, "name");
        if (string.IsNullOrWhiteSpace(toolName) || toolName.Contains('/') || toolName.Contains('\\') || toolName is "." or "..")
            throw new InvalidOperationException($"Steam CompatToolMapping[{SkyrimSeAppId}] has no safe selected tool name");
        if (selected.Children.TryGetValue("disabled", out var disabled) && disabled.Text is "1" or "true")
            throw new InvalidOperationException($"Steam CompatToolMapping[{SkyrimSeAppId}] marks selected tool '{toolName}' disabled");

        var librariesDoc = ReadVdf(librariesPath, "Steam libraryfolders.vdf");
        var librariesNode = Child(librariesDoc, "libraryfolders");
        var libraries = new HashSet<string>(PathComparer) { steamRoot };
        foreach (var (key, value) in librariesNode)
        {
            if (!int.TryParse(key, out _)) continue;
            var path = value.Children is null ? value.Text : Text(value.Children, "path");
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
                throw new InvalidOperationException($"Steam libraryfolders.vdf has an invalid path for library '{key}'");
            if (Directory.Exists(path)) libraries.Add(CanonicalDirectory(path));
        }
        var canonicalPrefix = RequireRealDirectory(prefix, "Amethyst selected prefix");
        var appData = Directory.GetParent(canonicalPrefix);
        if (!string.Equals(Path.GetFileName(canonicalPrefix), "pfx", StringComparison.Ordinal) ||
            !string.Equals(appData?.Name, SkyrimSeAppId, StringComparison.Ordinal) ||
            !string.Equals(appData?.Parent?.Name, "compatdata", StringComparison.Ordinal) ||
            !string.Equals(appData?.Parent?.Parent?.Name, "steamapps", StringComparison.Ordinal))
            throw new InvalidOperationException("selected prefix is not <Steam library>/steamapps/compatdata/489830/pfx; unsupported prefix layout");
        var libraryRoot = CanonicalDirectory(appData!.Parent!.Parent!.Parent!.FullName);
        if (!libraries.Contains(libraryRoot))
            throw new InvalidOperationException("selected prefix belongs to a library absent from Steam's current libraryfolders.vdf; refresh Steam's library configuration and Amethyst prefix selection");
        var compatData = RequireRealDirectory(appData.FullName, "selected Skyrim SE compatdata directory");
        var gameDir = LinuxFileIdentity.Canonical(snapshot.GameDir);
        var commonRoot = CanonicalDirectory(Path.Combine(libraryRoot, "steamapps", "common"));
        if (!IsUnder(commonRoot, gameDir) || !File.Exists(Path.Combine(gameDir, "SkyrimSE.exe")) ||
            new FileInfo(Path.Combine(gameDir, "SkyrimSE.exe")).LinkTarget is not null)
            throw new InvalidOperationException($"Amethyst's effective Skyrim SE game path '{snapshot.GameDir}' is not a real SkyrimSE.exe installation under the selected prefix library's steamapps/common; refusing to launch a different game's Proton context");

        var matches = new List<(string Library, string Directory, string Proton)>();
        foreach (var library in libraries.Order(PathComparer))
        {
            var common = Path.Combine(library, "steamapps", "common");
            if (Directory.Exists(common) && new DirectoryInfo(common).LinkTarget is null)
            {
                foreach (var directory in Directory.EnumerateDirectories(common)) AddDescriptorMatch(library, directory, toolName, matches);
            }
        }
        var customRoots = systemToolRoots.Append(Path.Combine(steamRoot, "compatibilitytools.d"));
        foreach (var customRoot in customRoots)
            if (Directory.Exists(customRoot) && new DirectoryInfo(customRoot).LinkTarget is null)
                foreach (var directory in Directory.EnumerateDirectories(customRoot))
                    AddDescriptorMatch(steamRoot, directory, toolName, matches);
        if (BuiltInToolDirectory(toolName) is { } builtInDirectory)
            foreach (var library in libraries)
            {
                var directory = Path.Combine(library, "steamapps", "common", builtInDirectory);
                var proton = Path.Combine(directory, "proton");
                if (Directory.Exists(directory) && new DirectoryInfo(directory).LinkTarget is null &&
                    File.Exists(proton) && new FileInfo(proton).LinkTarget is null)
                    matches.Add((library, Path.GetFullPath(directory), Path.GetFullPath(proton)));
            }
        var unique = matches.GroupBy(m => CanonicalFile(m.Proton), PathComparer).Select(g => g.First()).ToArray();
        if (unique.Length == 0)
            throw new InvalidOperationException($"Steam selects '{toolName}' for Skyrim SE, but no matching installed Proton compatibilitytool.vdf/proton executable exists in its listed libraries or user/system compatibility tool directories");
        if (unique.Length > 1)
            throw new InvalidOperationException($"Steam selects '{toolName}' for Skyrim SE, but multiple installed runners match across library, user or system tool directories; refusing to guess which copy Steam launches");
        return new SteamProtonSelection(steamRoot, unique[0].Library, toolName, unique[0].Directory, unique[0].Proton,
            canonicalPrefix, compatData, SkyrimSeAppId);
    }

    static string? BuiltInToolDirectory(string toolName)
    {
        if (toolName == "proton_experimental") return "Proton - Experimental";
        if (toolName == "proton_hotfix") return "Proton Hotfix";
        if (!toolName.StartsWith("proton_", StringComparison.Ordinal) ||
            !int.TryParse(toolName.AsSpan("proton_".Length), out var version) || version < 5) return null;
        return version switch { 5 => "Proton 5.13", 6 => "Proton 6.3", _ => $"Proton {version}.0" };
    }

    static void AddDescriptorMatch(string library, string directory, string requestedTool,
        List<(string Library, string Directory, string Proton)> matches)
    {
        if (new DirectoryInfo(directory).LinkTarget is not null) return;
        var descriptor = Path.Combine(directory, "compatibilitytool.vdf");
        if (!File.Exists(descriptor) || new FileInfo(descriptor).LinkTarget is not null) return;
        try
        {
            var tools = Child(Child(ReadVdf(descriptor, "compatibilitytool.vdf"), "compatibilitytools"), "compat_tools");
            if (!tools.TryGetValue(requestedTool, out var selected) || selected.Children is null) return;
            var installPath = Text(selected.Children, "install_path");
            if (string.IsNullOrWhiteSpace(installPath) || Path.IsPathRooted(installPath)) return;
            var canonicalRoot = CanonicalDirectory(directory);
            var toolDirectory = Path.GetFullPath(Path.Combine(canonicalRoot, installPath));
            if (!Directory.Exists(toolDirectory) || new DirectoryInfo(toolDirectory).LinkTarget is not null) return;
            // install_path may traverse symlinked intermediate components even when the leaf itself is ordinary.
            toolDirectory = CanonicalDirectory(toolDirectory);
            if (!IsUnder(canonicalRoot, toolDirectory)) return;
            var proton = Path.Combine(toolDirectory, "proton");
            if (File.Exists(proton) && new FileInfo(proton).LinkTarget is null)
                matches.Add((library, toolDirectory, Path.GetFullPath(proton)));
        }
        catch (InvalidOperationException) { /* unrelated or unsupported tool descriptor */ }
    }

    static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    static bool IsUnder(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative == "." || (relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    static string RequireRealDirectory(string path, string label)
    {
        var full = Path.GetFullPath(path);
        var current = new DirectoryInfo(full);
        if (!current.Exists || current.LinkTarget is not null) throw new InvalidOperationException($"{label} is missing or is a symlink: '{full}'");
        return CanonicalDirectory(full);
    }

    static string CanonicalDirectory(string path)
    {
        var info = new DirectoryInfo(Path.GetFullPath(path));
        var target = info.ResolveLinkTarget(returnFinalTarget: true);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(target?.FullName ?? info.FullName));
    }

    static string CanonicalFile(string path)
    {
        var info = new FileInfo(path);
        var target = info.ResolveLinkTarget(returnFinalTarget: true);
        return Path.GetFullPath(target?.FullName ?? info.FullName);
    }

    static Dictionary<string, VdfValue> ReadVdf(string path, string label)
    {
        if (!File.Exists(path) || new FileInfo(path).LinkTarget is not null) throw new InvalidOperationException($"{label} is missing or is a symlink: '{path}'");
        var source = File.ReadAllText(path, Encoding.UTF8);
        var matches = Tokens.Matches(source).Cast<Match>().ToArray();
        var cursor = 0;
        foreach (var match in matches)
        {
            if (match.Index != cursor) throw new InvalidOperationException($"{label} contains unsupported or malformed bytes at offset {cursor}");
            cursor = match.Index + match.Length;
        }
        if (source[cursor..].Any(c => !char.IsWhiteSpace(c)))
            throw new InvalidOperationException($"{label} contains unsupported or malformed bytes at offset {cursor}");
        var tokens = matches.Where(m => m.Groups[1].Success || m.Groups[2].Success)
            .Select(m => m.Groups[1].Success ? Unescape(m.Groups[1].Value) : m.Groups[2].Value).ToArray();
        int index = 0;
        Dictionary<string, VdfValue> Parse(bool nested)
        {
            var result = new Dictionary<string, VdfValue>(StringComparer.OrdinalIgnoreCase);
            while (index < tokens.Length)
            {
                if (tokens[index] == "}")
                {
                    if (!nested) throw new InvalidOperationException($"{label} has an unexpected closing brace");
                    index++; return result;
                }
                var key = tokens[index++];
                if (key == "{" || index >= tokens.Length || key.Length == 0) throw new InvalidOperationException($"{label} has malformed key/value syntax");
                if (tokens[index] == "{") { index++; result.Add(key, new VdfValue(null, Parse(true))); }
                else
                {
                    var value = tokens[index++];
                    if (value is "{" or "}") throw new InvalidOperationException($"{label} has malformed scalar value");
                    result.Add(key, new VdfValue(value, null));
                }
            }
            if (nested) throw new InvalidOperationException($"{label} has an unterminated object");
            return result;
        }
        return Parse(false);
    }

    static string Unescape(string text) => Regex.Replace(text, "\\\\(.)", m => m.Groups[1].Value);
    static Dictionary<string, VdfValue> Child(Dictionary<string, VdfValue> node, string name) =>
        node.TryGetValue(name, out var value) && value.Children is not null ? value.Children :
            throw new InvalidOperationException($"Steam VDF is missing object '{name}'");
    static string? Text(Dictionary<string, VdfValue> node, string name) =>
        node.TryGetValue(name, out var value) ? value.Text : null;
}
