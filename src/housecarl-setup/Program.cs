using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HousecarlSetup;

/// <summary>Installs the self-contained Linux server and its host integrations without root access.</summary>
public static class Program
{
    private const string ProductKey = "housecarl";
    private const string ProductDirectory = "housecarl-amethyst";

    /// <summary>AI host whose skills and MCP registration should be managed.</summary>
    public enum Target
    {
        /// <summary>Manage Claude Code only.</summary>
        Claude,
        /// <summary>Manage Codex only.</summary>
        Codex,
        /// <summary>Manage both supported hosts.</summary>
        Both,
    }

    /// <summary>Immutable native paths used by an install, rollback, or uninstall operation.</summary>
    /// <param name="Home">User home directory.</param>
    /// <param name="DataRoot">XDG product data directory.</param>
    /// <param name="ServerDir">Active self-contained server directory.</param>
    /// <param name="RollbackDir">Previous server directory retained for one-step rollback.</param>
    /// <param name="ConfigDir">Persistent XDG configuration directory.</param>
    /// <param name="ClaudeConfig">Claude Code JSON configuration file.</param>
    /// <param name="CodexConfig">Codex TOML configuration file.</param>
    public sealed record InstallPaths(
        string Home,
        string DataRoot,
        string ServerDir,
        string RollbackDir,
        string ConfigDir,
        string ClaudeConfig,
        string CodexConfig)
    {
        /// <summary>Builds absolute install paths from explicit XDG roots.</summary>
        public static InstallPaths Create(
            string home,
            string dataHome,
            string configHome,
            string? codexHome = null)
        {
            string fullHome = Path.GetFullPath(home);
            string dataRoot = Path.Combine(Path.GetFullPath(dataHome), ProductDirectory);
            string configRoot = Path.Combine(Path.GetFullPath(configHome), ProductDirectory);
            string resolvedCodexHome = string.IsNullOrWhiteSpace(codexHome)
                ? Path.Combine(fullHome, ".codex")
                : Path.GetFullPath(codexHome);
            return new(
                fullHome,
                dataRoot,
                Path.Combine(dataRoot, "server"),
                Path.Combine(dataRoot, "server.rollback"),
                configRoot,
                Path.Combine(fullHome, ".claude.json"),
                Path.Combine(resolvedCodexHome, "config.toml"));
        }
    }

    /// <summary>Runs the command-line installer and translates failures into actionable terminal output.</summary>
    private static int Main(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            PrintHelp();
            return 0;
        }
        if (!OperatingSystem.IsLinux())
        {
            Console.Error.WriteLine("error: houseCARL-Amethyst supports Linux x86_64 only.");
            return 1;
        }

        try
        {
            InstallPaths paths = EnvironmentPaths();
            Target? target = ResolveTarget(args);
            if (target is null)
            {
                Console.WriteLine("Cancelled; no files were changed.");
                return 0;
            }

            if (args.Contains("--uninstall"))
                Uninstall(target.Value, paths);
            else if (args.Contains("--rollback"))
                Rollback(paths);
            else
                Install(target.Value, AppContext.BaseDirectory, paths, verifyChecksums: true);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            return 1;
        }
    }

    /// <summary>Prints the supported non-interactive operations and their persistence behavior.</summary>
    private static void PrintHelp()
    {
        Console.WriteLine("houseCARL-Amethyst Linux installer");
        Console.WriteLine("  --codex | --claude | --both   choose host integration");
        Console.WriteLine("  --rollback                     restore the previous server version");
        Console.WriteLine("  --uninstall                    remove server, skills, and host registration");
        Console.WriteLine();
        Console.WriteLine("User configuration is kept under XDG_CONFIG_HOME during updates and uninstall.");
    }

    /// <summary>Resolves the current user's XDG and host-configuration locations.</summary>
    private static InstallPaths EnvironmentPaths()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
            throw new InvalidOperationException("HOME could not be resolved.");
        string dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME")
            ?? Path.Combine(home, ".local", "share");
        string configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
            ?? Path.Combine(home, ".config");
        return InstallPaths.Create(home, dataHome, configHome, Environment.GetEnvironmentVariable("CODEX_HOME"));
    }

    /// <summary>Returns an explicit target flag, or asks interactively when no flag was supplied.</summary>
    private static Target? ResolveTarget(string[] args)
    {
        List<Target> selected = [];
        if (args.Contains("--claude")) selected.Add(Target.Claude);
        if (args.Contains("--codex")) selected.Add(Target.Codex);
        if (args.Contains("--both")) selected.Add(Target.Both);
        if (selected.Count > 1)
            throw new ArgumentException("choose only one of --claude, --codex, or --both.");
        if (selected.Count == 1)
            return selected[0];

        Console.Write("Install for [1] Claude Code, [2] Codex, [3] both, or [q] quit: ");
        return Console.ReadLine()?.Trim().ToLowerInvariant() switch
        {
            "1" => Target.Claude,
            "2" => Target.Codex,
            "3" => Target.Both,
            "q" or "quit" or null => null,
            _ => throw new ArgumentException("expected 1, 2, 3, or q."),
        };
    }

    /// <summary>
    /// Verifies the release payload, atomically replaces the shared server, then registers the selected hosts.
    /// </summary>
    /// <param name="target">Host integration to install.</param>
    /// <param name="packageRoot">Extracted release directory containing housecarl/server.</param>
    /// <param name="paths">Explicit native destination paths.</param>
    /// <param name="verifyChecksums">Whether SHA256SUMS must validate before any mutation.</param>
    public static void Install(Target target, string packageRoot, InstallPaths paths, bool verifyChecksums)
    {
        string root = Path.GetFullPath(packageRoot);
        string plugin = Path.Combine(root, ProductKey);
        string server = Path.Combine(plugin, "server");
        string executable = Path.Combine(server, "housecarl-mcp");
        if (!File.Exists(executable))
            throw new FileNotFoundException("release payload is missing housecarl/server/housecarl-mcp.", executable);
        if (verifyChecksums)
            VerifyChecksums(root);

        ReplaceServer(server, paths.ServerDir, paths.RollbackDir);
        Directory.CreateDirectory(paths.ConfigDir);
        string installedExecutable = Path.Combine(paths.ServerDir, "housecarl-mcp");
        string skills = Path.Combine(plugin, "skills");
        string umbrella = Path.Combine(root, "codex", ProductKey);

        if (target is Target.Claude or Target.Both)
        {
            InstallSkills(skills, umbrella, Path.Combine(paths.Home, ".claude", "skills"));
            RegisterClaude(paths.ClaudeConfig, installedExecutable, paths.ConfigDir);
        }
        if (target is Target.Codex or Target.Both)
        {
            InstallSkills(skills, umbrella, Path.Combine(paths.Home, ".agents", "skills"));
            RegisterCodex(paths.CodexConfig, installedExecutable, paths.ConfigDir);
        }

        Console.WriteLine($"Installed houseCARL-Amethyst server: {paths.ServerDir}");
        Console.WriteLine($"Persistent configuration: {paths.ConfigDir}");
        Console.WriteLine("Restart the selected host, connect an Amethyst manifest, then run housecarl_amethyst_status.");
    }

    /// <summary>Restores the immediately previous server directory and retains the replaced build as rollback.</summary>
    public static void Rollback(InstallPaths paths)
    {
        if (!Directory.Exists(paths.RollbackDir))
            throw new InvalidOperationException("no previous server version is available to roll back.");
        string swap = paths.ServerDir + ".swap-" + Guid.NewGuid().ToString("N");
        if (Directory.Exists(paths.ServerDir))
            Directory.Move(paths.ServerDir, swap);
        try
        {
            Directory.Move(paths.RollbackDir, paths.ServerDir);
            if (Directory.Exists(swap))
                Directory.Move(swap, paths.RollbackDir);
        }
        catch
        {
            if (!Directory.Exists(paths.ServerDir) && Directory.Exists(swap))
                Directory.Move(swap, paths.ServerDir);
            throw;
        }
        Console.WriteLine($"Restored previous server: {paths.ServerDir}");
    }

    /// <summary>Removes installed binaries, owned skills, and selected host registrations while preserving user config.</summary>
    public static void Uninstall(Target target, InstallPaths paths)
    {
        if (target is Target.Claude or Target.Both)
        {
            RemoveOwnedSkills(Path.Combine(paths.Home, ".claude", "skills"));
            RemoveClaudeRegistration(paths.ClaudeConfig);
        }
        if (target is Target.Codex or Target.Both)
        {
            RemoveOwnedSkills(Path.Combine(paths.Home, ".agents", "skills"));
            RemoveCodexRegistration(paths.CodexConfig);
        }
        DeleteDirectory(paths.ServerDir);
        DeleteDirectory(paths.RollbackDir);
        Console.WriteLine($"Uninstalled houseCARL-Amethyst; preserved configuration: {paths.ConfigDir}");
    }

    /// <summary>Validates every relative payload hash listed in SHA256SUMS.</summary>
    public static void VerifyChecksums(string packageRoot)
    {
        string root = Path.GetFullPath(packageRoot);
        string list = Path.Combine(root, "SHA256SUMS");
        if (!File.Exists(list))
            throw new FileNotFoundException("release is missing SHA256SUMS.", list);

        int count = 0;
        foreach (string raw in File.ReadLines(list))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            int split = line.IndexOf("  ", StringComparison.Ordinal);
            if (split != 64)
                throw new InvalidDataException($"malformed SHA256SUMS line: {raw}");
            string relative = line[(split + 2)..];
            string path = SafePackagePath(root, relative);
            if (!File.Exists(path))
                throw new InvalidDataException($"checksummed payload is missing: {relative}");
            string actual = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
            if (!actual.Equals(line[..64], StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"checksum mismatch: {relative}");
            count++;
        }
        if (count == 0)
            throw new InvalidDataException("SHA256SUMS contains no payload entries.");
    }

    /// <summary>Resolves a checksum entry beneath the package root and rejects traversal or absolute paths.</summary>
    private static string SafePackagePath(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains('\0'))
            throw new InvalidDataException($"unsafe checksum path: {relative}");
        string path = Path.GetFullPath(Path.Combine(root, relative));
        string prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidDataException($"checksum path escapes the release: {relative}");
        return path;
    }

    /// <summary>Copies the server to a sibling staging directory and swaps it into place by directory rename.</summary>
    private static void ReplaceServer(string source, string destination, string rollback)
    {
        string? parent = Path.GetDirectoryName(destination);
        if (parent is null) throw new InvalidOperationException("server destination has no parent.");
        Directory.CreateDirectory(parent);
        string staging = destination + ".installing-" + Guid.NewGuid().ToString("N");
        CopyDirectory(source, staging);
        try
        {
            DeleteDirectory(rollback);
            if (Directory.Exists(destination))
                Directory.Move(destination, rollback);
            try { Directory.Move(staging, destination); }
            catch
            {
                if (!Directory.Exists(destination) && Directory.Exists(rollback))
                    Directory.Move(rollback, destination);
                throw;
            }
        }
        finally { DeleteDirectory(staging); }
    }

    /// <summary>Installs all bundled helper skills plus the host-neutral umbrella skill.</summary>
    private static void InstallSkills(string skillsSource, string umbrellaSource, string destinationRoot)
    {
        Directory.CreateDirectory(destinationRoot);
        List<string> owned = [];
        if (Directory.Exists(skillsSource))
            foreach (string source in Directory.GetDirectories(skillsSource))
            {
                string name = Path.GetFileName(source);
                ReplaceOwnedDirectory(source, Path.Combine(destinationRoot, name));
                owned.Add(name);
            }
        if (Directory.Exists(umbrellaSource))
        {
            ReplaceOwnedDirectory(umbrellaSource, Path.Combine(destinationRoot, ProductKey));
            owned.Add(ProductKey);
        }
        File.WriteAllLines(
            Path.Combine(destinationRoot, ProductKey, ".housecarl-owned-skills"),
            owned.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
    }

    /// <summary>Replaces one installer-owned skill directory through a sibling staging directory.</summary>
    private static void ReplaceOwnedDirectory(string source, string destination)
    {
        string staging = destination + ".installing-" + Guid.NewGuid().ToString("N");
        CopyDirectory(source, staging);
        try
        {
            DeleteDirectory(destination);
            Directory.Move(staging, destination);
        }
        finally { DeleteDirectory(staging); }
    }

    /// <summary>Removes only skill directories named in the installed ownership manifest.</summary>
    private static void RemoveOwnedSkills(string root)
    {
        string manifest = Path.Combine(root, ProductKey, ".housecarl-owned-skills");
        if (File.Exists(manifest))
            foreach (string name in File.ReadLines(manifest).Where(IsSimpleName).ToArray())
                DeleteDirectory(Path.Combine(root, name));
        DeleteDirectory(Path.Combine(root, ProductKey));
    }

    /// <summary>Returns true for a single safe directory name used by the ownership manifest.</summary>
    private static bool IsSimpleName(string name) =>
        name.Length > 0 && name is not "." and not ".." &&
        name.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '\0']) < 0;

    /// <summary>Recursively copies a directory without following directory symlinks.</summary>
    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"release contains a directory symlink: {directory}");
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }
        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"release contains a file symlink: {file}");
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
        }
    }

    /// <summary>Deletes a directory when it exists.</summary>
    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }

    /// <summary>Registers the shared server and persistent data directory in Claude Code's JSON config.</summary>
    private static void RegisterClaude(string path, string command, string dataDir)
    {
        JsonObject entry = new()
        {
            ["type"] = "stdio",
            ["command"] = command,
            ["args"] = new JsonArray(),
            ["env"] = new JsonObject { ["HOUSECARL_DATA_DIR"] = dataDir },
        };
        JsonObject root = ReadJsonObject(path);
        JsonObject servers = root["mcpServers"] as JsonObject ?? new JsonObject();
        root["mcpServers"] = servers;
        servers[ProductKey] = entry;
        WriteConfig(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Removes only houseCARL's entry from Claude Code's JSON config.</summary>
    private static void RemoveClaudeRegistration(string path)
    {
        if (!File.Exists(path)) return;
        JsonObject root = ReadJsonObject(path);
        if (root["mcpServers"] is JsonObject servers)
            servers.Remove(ProductKey);
        WriteConfig(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Reads a JSON object or creates an empty root when the config does not exist.</summary>
    private static JsonObject ReadJsonObject(string path) =>
        !File.Exists(path) ? new JsonObject() :
        JsonNode.Parse(File.ReadAllText(path)) as JsonObject
            ?? throw new InvalidDataException($"{path} is not a JSON object.");

    /// <summary>Registers the shared server and environment in Codex without reformatting unrelated TOML.</summary>
    private static void RegisterCodex(string path, string command, string dataDir)
    {
        string text = File.Exists(path) ? File.ReadAllText(path) : "";
        string body = $"[mcp_servers.{ProductKey}]\n" +
                      $"command = {TomlString(command)}\n\n" +
                      $"[mcp_servers.{ProductKey}.env]\n" +
                      $"HOUSECARL_DATA_DIR = {TomlString(dataDir)}\n";
        WriteConfig(path, ReplaceTomlTable(text, body));
    }

    /// <summary>Removes houseCARL's Codex table and nested environment table.</summary>
    private static void RemoveCodexRegistration(string path)
    {
        if (!File.Exists(path)) return;
        WriteConfig(path, ReplaceTomlTable(File.ReadAllText(path), null));
    }

    /// <summary>Replaces or removes the product's TOML table while preserving every unrelated line.</summary>
    private static string ReplaceTomlTable(string text, string? replacement)
    {
        string newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        List<string> kept = [];
        for (int i = 0; i < lines.Length;)
        {
            if (!IsOwnedTomlHeader(lines[i]))
            {
                kept.Add(lines[i++]);
                continue;
            }
            i++;
            while (i < lines.Length && !lines[i].TrimStart().StartsWith("[", StringComparison.Ordinal))
                i++;
        }
        string result = string.Join(newline, kept).TrimEnd();
        if (replacement is not null)
            result = (result.Length == 0 ? "" : result + newline + newline) +
                     replacement.Replace("\n", newline, StringComparison.Ordinal).TrimEnd();
        return result.Length == 0 ? "" : result + newline;
    }

    /// <summary>Recognizes the exact product table or one of its nested tables without matching similarly named servers.</summary>
    private static bool IsOwnedTomlHeader(string line)
    {
        string header = line.Trim();
        return header == $"[mcp_servers.{ProductKey}]" ||
               header.StartsWith($"[mcp_servers.{ProductKey}.", StringComparison.Ordinal);
    }

    /// <summary>Quotes a native path as a TOML basic string, including apostrophes and Unicode safely.</summary>
    private static string TomlString(string value)
    {
        StringBuilder quoted = new StringBuilder(value.Length + 2).Append('"');
        foreach (char character in value)
            quoted.Append(character switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\b' => "\\b",
                '\t' => "\\t",
                '\n' => "\\n",
                '\f' => "\\f",
                '\r' => "\\r",
                < ' ' or '\u007f' => "\\u" + ((int)character).ToString("X4"),
                _ => character.ToString(),
            });
        return quoted.Append('"').ToString();
    }

    /// <summary>Backs up an existing host config, then atomically replaces it with UTF-8 text.</summary>
    private static void WriteConfig(string path, string text)
    {
        string? directory = Path.GetDirectoryName(path);
        if (directory is null) throw new InvalidOperationException($"config path has no parent: {path}");
        Directory.CreateDirectory(directory);
        if (File.Exists(path))
            File.Copy(path, path + ".housecarl-amethyst.bak", overwrite: true);
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, text);
        File.Move(temporary, path, overwrite: true);
    }
}
