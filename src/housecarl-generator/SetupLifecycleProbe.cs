using System.Text.Json.Nodes;
using SetupProgram = HousecarlSetup.Program;

namespace HousecarlGenerator;

/// <summary>Proves Linux install, upgrade, rollback, checksum, host registration, and conservative uninstall.</summary>
internal static class SetupLifecycleProbe
{
    /// <summary>Runs the hermetic setup lifecycle against a temporary home and synthetic release.</summary>
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("==============================================================");
        Console.WriteLine(" houseCARL-Amethyst Linux setup lifecycle guard");
        Console.WriteLine("==============================================================");
        int failures = 0;
        void Check(bool condition, string label)
        {
            Console.WriteLine((condition ? "  PASS  " : "  FAIL  ") + label);
            if (!condition) failures++;
        }

        string root = Path.Combine(Path.GetTempPath(), "housecarl-setup-" + Guid.NewGuid().ToString("N"));
        string package = Path.Combine(root, "release");
        string home = Path.Combine(root, "home with space and ' quote", "使用者");
        var paths = SetupProgram.InstallPaths.Create(
            home,
            Path.Combine(home, ".local", "share"),
            Path.Combine(home, ".config"));
        try
        {
            WriteRelease(package, "version-one");
            SetupProgram.Install(SetupProgram.Target.Both, package, paths, verifyChecksums: true);

            string installed = Path.Combine(paths.ServerDir, "housecarl-mcp");
            string config = Path.Combine(paths.ConfigDir, "houseCARL.user.json");
            Check(File.ReadAllText(installed) == "version-one", "clean install copies the native server");
            Check(ClaudeCommand(paths.ClaudeConfig) == installed, "Claude registration uses the shared native executable");
            Check(File.ReadAllText(paths.CodexConfig).Contains($"command = \"{installed}\"", StringComparison.Ordinal),
                "Codex registration uses the shared native executable");
            Check(File.ReadAllText(paths.CodexConfig).Contains($"HOUSECARL_DATA_DIR = \"{paths.ConfigDir}\"", StringComparison.Ordinal),
                "Codex registration keeps mutable state under XDG_CONFIG_HOME");
            File.AppendAllText(paths.CodexConfig, "\n[mcp_servers.housecarl-helper]\ncommand = \"keep\"\n");

            Directory.CreateDirectory(paths.ConfigDir);
            File.WriteAllText(config, "persistent-connection");
            WriteRelease(package, "version-two");
            SetupProgram.Install(SetupProgram.Target.Both, package, paths, verifyChecksums: true);
            Check(File.ReadAllText(installed) == "version-two", "upgrade activates the new server");
            Check(File.ReadAllText(Path.Combine(paths.RollbackDir, "housecarl-mcp")) == "version-one",
                "upgrade retains exactly one previous server");
            Check(File.ReadAllText(config) == "persistent-connection", "upgrade preserves user configuration");

            SetupProgram.Rollback(paths);
            Check(File.ReadAllText(installed) == "version-one", "rollback restores the previous server");
            Check(File.ReadAllText(Path.Combine(paths.RollbackDir, "housecarl-mcp")) == "version-two",
                "rollback remains reversible");

            File.AppendAllText(Path.Combine(package, "housecarl", "server", "housecarl-mcp"), "tampered");
            bool checksumRejected = false;
            try { SetupProgram.VerifyChecksums(package); }
            catch (InvalidDataException) { checksumRejected = true; }
            Check(checksumRejected, "checksum verification rejects a modified payload");

            SetupProgram.Uninstall(SetupProgram.Target.Both, paths);
            Check(!Directory.Exists(paths.ServerDir) && !Directory.Exists(paths.RollbackDir),
                "uninstall removes active and rollback server trees");
            Check(File.ReadAllText(config) == "persistent-connection", "uninstall preserves user configuration");
            Check(ClaudeCommand(paths.ClaudeConfig) is null, "uninstall removes only the Claude MCP registration");
            Check(!File.ReadAllText(paths.CodexConfig).Contains("[mcp_servers.housecarl]", StringComparison.Ordinal),
                "uninstall removes the Codex MCP tables");
            Check(File.ReadAllText(paths.CodexConfig).Contains("[mcp_servers.housecarl-helper]", StringComparison.Ordinal),
                "uninstall preserves similarly named Codex servers");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }

        Console.WriteLine(failures == 0
            ? "================ ALL PASS ================"
            : $"================ {failures} CHECK(S) FAILED ================");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>Creates a minimal release and a matching SHA256SUMS file.</summary>
    private static void WriteRelease(string root, string serverBytes)
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        Write(Path.Combine(root, "housecarl", "server", "housecarl-mcp"), serverBytes);
        Write(Path.Combine(root, "housecarl", "skills", "demo-skill", "SKILL.md"), "demo");
        Write(Path.Combine(root, "codex", "housecarl", "SKILL.md"), "umbrella");
        string hash = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(
                File.ReadAllBytes(Path.Combine(root, "housecarl", "server", "housecarl-mcp"))));
        Write(Path.Combine(root, "SHA256SUMS"), hash + "  housecarl/server/housecarl-mcp\n");
    }

    /// <summary>Returns Claude's configured command, or null when houseCARL is not registered.</summary>
    private static string? ClaudeCommand(string path) =>
        (JsonNode.Parse(File.ReadAllText(path))?["mcpServers"]?["housecarl"]?["command"] as JsonValue)
        ?.GetValue<string>();

    /// <summary>Writes a fixture file after creating its parent directory.</summary>
    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
}
